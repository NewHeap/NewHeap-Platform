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
    private NhTwoFactorRequirement(bool required, IReadOnlyList<string> allowedMethods)
    {
        Required = required;
        AllowedMethods = allowedMethods;
    }

    public static NhTwoFactorRequirement NotRequired { get; } = new(false, []);

    /// <summary>Whether the user must present a second factor.</summary>
    public bool Required { get; }

    /// <summary>Methods that satisfy the requirement, see <see cref="NhTwoFactorMethods"/>.</summary>
    public IReadOnlyList<string> AllowedMethods { get; }

    public static NhTwoFactorRequirement RequiredWith(IEnumerable<string> allowedMethods)
    {
        ArgumentNullException.ThrowIfNull(allowedMethods);
        return new NhTwoFactorRequirement(true, allowedMethods.Distinct(StringComparer.Ordinal).ToList());
    }
}

/// <summary>
/// Default policy: users who enrolled a second factor must use it. A Microsoft OAuth
/// sign-in is accepted without a NewHeap second factor because the identity provider
/// enforces its own MFA policy.
/// </summary>
public class NhDefaultTwoFactorPolicy : INhTwoFactorPolicy
{
    public virtual Task<NhTwoFactorRequirement> EvaluateAsync(
        NhTwoFactorPolicyContext context,
        CancellationToken cancellationToken = default)
    {
        if (!context.IsEnrolled)
        {
            return Task.FromResult(NhTwoFactorRequirement.NotRequired);
        }

        if (context.Factor == NhAuthenticationFactors.MicrosoftOAuth)
        {
            return Task.FromResult(NhTwoFactorRequirement.NotRequired);
        }

        return Task.FromResult(NhTwoFactorRequirement.RequiredWith(context.EnrolledMethods));
    }
}
