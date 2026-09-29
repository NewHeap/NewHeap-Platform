using Microsoft.AspNetCore.Identity;
using NewHeap.Platform.AspNet.Common.Models;

namespace NewHeap.Platform.AspNet.Common.Authentication.TwoFactor;

/// <summary>
/// Evidence that every factor required for a user was verified by NewHeap. Only library
/// verification paths can create a proof, so a session issued with a proof cannot skip a
/// required second factor.
/// </summary>
public sealed class NhAuthenticationProof
{
    internal NhAuthenticationProof(Guid userId, IReadOnlyList<string> factors)
    {
        UserId = userId;
        Factors = factors;
    }

    public Guid UserId { get; }

    /// <summary>The verified factors, see <see cref="NhAuthenticationFactors"/> and <see cref="NhTwoFactorMethods"/>.</summary>
    public IReadOnlyList<string> Factors { get; }
}

/// <summary>
/// Two-factor collaborators for <c>NhAuthenticationService</c>. Resolve it from dependency
/// injection and pass it to the service constructor; it is registered by
/// <c>AddAuthentication</c> whether or not two-factor authentication is enabled.
/// </summary>
public sealed class NhTwoFactorAuthenticationContext<TUser>
    where TUser : IdentityUser<Guid>
{
    internal NhTwoFactorAuthenticationContext(
        NhTwoFactorConfiguration configuration,
        NhTwoFactorService<TUser>? service)
    {
        Configuration = configuration;
        Service = service;
    }

    public NhTwoFactorConfiguration Configuration { get; }

    internal NhTwoFactorService<TUser>? Service { get; }

    internal bool IsEnabled => Configuration.Enabled && Service != null;
}

/// <summary>
/// Session and recovery codes issued after a required user enrolled a second factor during
/// sign-in.
/// </summary>
public sealed class NhTwoFactorEnrollmentCompletion
{
    public NhTwoFactorEnrollmentCompletion(UserToken session, IReadOnlyList<string>? recoveryCodes)
    {
        ArgumentNullException.ThrowIfNull(session);
        Session = session;
        RecoveryCodes = recoveryCodes;
    }

    public UserToken Session { get; }

    /// <summary>Recovery codes to show once, if the enrollment issued them.</summary>
    public IReadOnlyList<string>? RecoveryCodes { get; }
}

/// <summary>
/// A pending challenge or enrollment that still belongs to the account state.
/// </summary>
internal sealed record NhPendingTwoFactorStep<TUser>(TUser User, NhTwoFactorTicket Ticket);
