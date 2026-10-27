using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using NewHeap.Platform.AspNet.Common.Services;
using NewHeap.Platform.Common.Identity.Claims;
using System.Security.Claims;

namespace NewHeap.Platform.AspNet.Common.Authentication;

/// <summary>
/// Validates that a cryptographically valid principal still belongs to an active
/// NewHeap authentication session.
/// </summary>
public interface INhAuthenticationSessionValidator
{
    /// <summary>
    /// Validates the current user and Identity security stamp represented by the principal, and for
    /// an impersonation also the security stamp and block of the user who started it.
    /// </summary>
    /// <param name="principal">Principal produced by token validation.</param>
    /// <param name="cancellationToken">Cancellation token for the current request.</param>
    /// <returns><see langword="true"/> when the session remains valid.</returns>
    Task<bool> ValidateAsync(
        ClaimsPrincipal principal,
        CancellationToken cancellationToken = default);
}

internal static class NhAuthenticationSessionDefaults
{
    internal const string LoginProvider = "NewHeap.Platform.AspNet.Common";
    internal const string SecurityStampTokenName = "AuthenticationSessionSecurityStamp";
}

internal sealed class NhAuthenticationSessionValidator<TUser> : INhAuthenticationSessionValidator
    where TUser : IdentityUser<Guid>
{
    private readonly UserManager<TUser> _userManager;

    internal NhAuthenticationSessionValidator(UserManager<TUser> userManager)
    {
        _userManager = userManager;
    }

    public async Task<bool> ValidateAsync(
        ClaimsPrincipal principal,
        CancellationToken cancellationToken = default)
    {
        var userIdValue = principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (!Guid.TryParse(userIdValue, out var userId))
        {
            // Preserve compatibility with signed consumer tokens that do not use
            // NewHeap's user-id claim. NewHeap-issued tokens always contain it.
            return true;
        }

        var user = await _userManager.FindByIdAsync(userId.ToString());
        if (user == null)
        {
            return false;
        }

        cancellationToken.ThrowIfCancellationRequested();

        if (!await IsUserSessionValidAsync(principal, user))
        {
            return false;
        }

        return await IsImpersonationOriginValidAsync(principal, cancellationToken);
    }

    private async Task<bool> IsUserSessionValidAsync(ClaimsPrincipal principal, TUser user)
    {
        var securityStampClaimType = _userManager.Options.ClaimsIdentity.SecurityStampClaimType;
        var tokenSecurityStamp = principal.FindFirst(securityStampClaimType)?.Value;
        if (!string.IsNullOrEmpty(tokenSecurityStamp))
        {
            var currentSecurityStamp = await _userManager.GetSecurityStampAsync(user);
            return string.Equals(tokenSecurityStamp, currentSecurityStamp, StringComparison.Ordinal);
        }

        var sessionSecurityStamp = await _userManager.GetAuthenticationTokenAsync(
            user,
            NhAuthenticationSessionDefaults.LoginProvider,
            NhAuthenticationSessionDefaults.SecurityStampTokenName);

        // Tokens issued before this feature do not contain a security stamp. They
        // remain valid until the first password mutation records a session marker.
        return sessionSecurityStamp == null;
    }

    /// <summary>
    /// An impersonation session belongs to the session of the user who started it: it ends when
    /// that user's security stamp changes, for example after a password change, and while that user
    /// is locked out or blocked. An impersonation token without the origin's stamp, issued before the
    /// session was bound to it, is no longer accepted.
    /// </summary>
    private async Task<bool> IsImpersonationOriginValidAsync(
        ClaimsPrincipal principal,
        CancellationToken cancellationToken)
    {
        var originUserIdValue = principal.FindFirst(NhPlatformClaimTypes.ImpersonateOriginUserId)?.Value;
        if (originUserIdValue == null)
        {
            return true;
        }

        var originSecurityStamp = principal.FindFirst(NhPlatformClaimTypes.ImpersonateOriginSecurityStamp)?.Value;
        if (!Guid.TryParse(originUserIdValue, out var originUserId) || string.IsNullOrEmpty(originSecurityStamp))
        {
            return false;
        }

        var origin = await _userManager.FindByIdAsync(originUserId.ToString());
        if (origin == null)
        {
            return false;
        }

        cancellationToken.ThrowIfCancellationRequested();

        var currentOriginSecurityStamp = await _userManager.GetSecurityStampAsync(origin);
        if (!string.Equals(originSecurityStamp, currentOriginSecurityStamp, StringComparison.Ordinal))
        {
            return false;
        }

        return !await IsBlockedAsync(origin);
    }

    private async Task<bool> IsBlockedAsync(TUser user)
    {
        // The NewHeap user manager also sees the administrative block (LockoutStart), which Identity's
        // lockout does not.
        if (_userManager is INhUserManager<TUser> nhUserManager)
        {
            return await nhUserManager.IsBlockedAsync(user);
        }

        return await _userManager.IsLockedOutAsync(user);
    }
}

internal static class NhAuthenticationEvents
{
    internal static void Compose(
        JwtBearerOptions options,
        Func<MessageReceivedContext, Task> onMessageReceived,
        Func<TokenValidatedContext, Task> onTokenValidated)
    {
        var events = options.Events ?? new JwtBearerEvents();
        var consumerMessageReceived = events.OnMessageReceived;
        var consumerTokenValidated = events.OnTokenValidated;

        events.OnMessageReceived = async context =>
        {
            await onMessageReceived(context);

            if (consumerMessageReceived != null)
            {
                await consumerMessageReceived(context);
            }
        };

        events.OnTokenValidated = async context =>
        {
            if (consumerTokenValidated != null)
            {
                await consumerTokenValidated(context);
            }

            await onTokenValidated(context);
        };

        options.Events = events;
    }
}
