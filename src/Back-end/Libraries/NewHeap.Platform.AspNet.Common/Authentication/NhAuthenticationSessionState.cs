using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using System.Security.Claims;

namespace NewHeap.Platform.AspNet.Common.Authentication;

/// <summary>
/// Validates that a cryptographically valid principal still belongs to an active
/// NewHeap authentication session.
/// </summary>
public interface INhAuthenticationSessionValidator
{
    /// <summary>
    /// Validates the current user and Identity security stamp represented by the principal.
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
