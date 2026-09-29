using NewHeap.Platform.Common.Identity.Claims;
using System.Security.Claims;

namespace NewHeap.Platform.AspNet.Common.Authentication.TwoFactor;

/// <summary>
/// Decides whether a user needs a second factor after a first factor succeeded.
/// </summary>
/// <remarks>
/// The policy runs for every session source: password, external sign-in, consumer
/// credentials, trusted sign-in and refresh-token rotation. A policy that requires a
/// second factor for a user without an enrolled method blocks the session.
/// </remarks>
public interface INhTwoFactorPolicy
{
    Task<NhTwoFactorRequirement> EvaluateAsync(
        NhTwoFactorPolicyContext context,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Input for <see cref="INhTwoFactorPolicy"/>.
/// </summary>
public sealed class NhTwoFactorPolicyContext
{
    private readonly Func<CancellationToken, Task<IReadOnlyList<Claim>>> _claimsLoader;
    private IReadOnlyList<Claim>? _claims;

    internal NhTwoFactorPolicyContext(
        Guid userId,
        string factor,
        bool isEnrolled,
        IReadOnlyList<string> enrolledMethods,
        Func<CancellationToken, Task<IReadOnlyList<Claim>>> claimsLoader)
    {
        UserId = userId;
        Factor = factor;
        IsEnrolled = isEnrolled;
        EnrolledMethods = enrolledMethods;
        _claimsLoader = claimsLoader;
    }

    public Guid UserId { get; }

    /// <summary>The factor that started this step, see <see cref="NhAuthenticationFactors"/>.</summary>
    public string Factor { get; }

    /// <summary>Whether the user enabled two-factor authentication with a usable method.</summary>
    public bool IsEnrolled { get; }

    /// <summary>The user's enrolled second-factor methods.</summary>
    public IReadOnlyList<string> EnrolledMethods { get; }

    /// <summary>
    /// Loads the user's application claims, including roles and permissions. The claims are
    /// loaded once per evaluation and only when a policy asks for them.
    /// </summary>
    public async Task<IReadOnlyList<Claim>> GetClaimsAsync(CancellationToken cancellationToken = default)
    {
        if (_claims == null)
        {
            _claims = await _claimsLoader(cancellationToken);
        }

        return _claims;
    }
}

/// <summary>
/// Outcome of <see cref="INhTwoFactorPolicy"/>.
/// </summary>
public sealed class NhTwoFactorRequirement
{
    private NhTwoFactorRequirement(
        bool required,
        IReadOnlyList<string> allowedMethods,
        bool enforcedByPolicy,
        bool allowRememberDevice)
    {
        Required = required;
        AllowedMethods = allowedMethods;
        EnforcedByPolicy = enforcedByPolicy;
        AllowRememberDevice = allowRememberDevice;
    }

    public static NhTwoFactorRequirement NotRequired { get; } = new(false, [], false, true);

    /// <summary>Whether the user must present a second factor.</summary>
    public bool Required { get; }

    /// <summary>Methods that satisfy the requirement, see <see cref="NhTwoFactorMethods"/>.</summary>
    public IReadOnlyList<string> AllowedMethods { get; }

    /// <summary>
    /// Whether the policy requires the second factor regardless of the user's own choice.
    /// Such a user must enroll before receiving a session and cannot disable the factor.
    /// </summary>
    public bool EnforcedByPolicy { get; }

    /// <summary>Whether a remembered device may skip the second factor.</summary>
    public bool AllowRememberDevice { get; }

    public static NhTwoFactorRequirement RequiredWith(
        IEnumerable<string> allowedMethods,
        bool enforcedByPolicy = false,
        bool allowRememberDevice = true)
    {
        ArgumentNullException.ThrowIfNull(allowedMethods);
        return new NhTwoFactorRequirement(
            true,
            allowedMethods.Distinct(StringComparer.Ordinal).ToList(),
            enforcedByPolicy,
            allowRememberDevice);
    }
}

/// <summary>
/// Default policy. Users who enrolled a second factor must use it, and the requirements
/// configured with <see cref="NhTwoFactorBuilder.RequireFor"/> (roles, permissions or all
/// users) enforce it for everyone they match. A Microsoft OAuth sign-in is accepted without
/// a NewHeap second factor unless the requirements include external providers.
/// </summary>
public class NhDefaultTwoFactorPolicy : INhTwoFactorPolicy
{
    private readonly NhTwoFactorConfiguration? _configuration;

    public NhDefaultTwoFactorPolicy()
    {
    }

    public NhDefaultTwoFactorPolicy(NhTwoFactorConfiguration configuration)
    {
        _configuration = configuration;
    }

    public virtual async Task<NhTwoFactorRequirement> EvaluateAsync(
        NhTwoFactorPolicyContext context,
        CancellationToken cancellationToken = default)
    {
        var externalProvidersSatisfy = _configuration?.ExternalProvidersSatisfyRequirement ?? true;
        if (context.Factor == NhAuthenticationFactors.MicrosoftOAuth && externalProvidersSatisfy)
        {
            return NhTwoFactorRequirement.NotRequired;
        }

        var enforced = await IsEnforcedAsync(context, cancellationToken);
        if (!enforced && !context.IsEnrolled)
        {
            return NhTwoFactorRequirement.NotRequired;
        }

        var emailSatisfies = !enforced || _configuration?.EmailSatisfiesRequirement == true;
        var allowedMethods = context.EnrolledMethods
            .Where(method => method != NhTwoFactorMethods.Email || emailSatisfies)
            .ToList();

        var allowRememberDevice = !enforced || _configuration?.RememberDeviceForRequiredUsers != false;

        return NhTwoFactorRequirement.RequiredWith(allowedMethods, enforced, allowRememberDevice);
    }

    /// <summary>
    /// Returns whether the configured requirements match the user: every user, one of the
    /// required application roles or one of the required application permissions.
    /// </summary>
    protected virtual async Task<bool> IsEnforcedAsync(
        NhTwoFactorPolicyContext context,
        CancellationToken cancellationToken)
    {
        if (_configuration == null || !_configuration.HasRequirements)
        {
            return false;
        }

        if (_configuration.RequireForAllUsers)
        {
            return true;
        }

        var claims = await context.GetClaimsAsync(cancellationToken);
        return claims.Any(claim =>
            (claim.Type == ClaimTypes.Role && _configuration.RequiredRoles.Contains(claim.Value, StringComparer.Ordinal))
            || (claim.Type == NhPlatformClaimTypes.Permission
                && _configuration.RequiredPermissions.Contains(claim.Value, StringComparer.Ordinal)));
    }
}
