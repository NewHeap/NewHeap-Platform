using NewHeap.Platform.AspNet.Common.Models;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace NewHeap.Platform.AspNet.Common.Authentication.TwoFactor;

/// <summary>
/// A pending authentication step that the client completes with a second factor.
/// </summary>
public sealed class NhTwoFactorChallengeResponse
{
    /// <summary>The step status, see <see cref="NhAuthenticationStepStatuses"/>.</summary>
    public required string Status { get; init; }

    /// <summary>Opaque, short-lived and single-use token that identifies the pending step.</summary>
    public required string ChallengeToken { get; init; }

    /// <summary>Moment after which the challenge can no longer be completed.</summary>
    public required DateTimeOffset ExpiresAt { get; init; }

    /// <summary>Second-factor methods the user may present, see <see cref="NhTwoFactorMethods"/>.</summary>
    public required IReadOnlyList<string> Methods { get; init; }
}

/// <summary>
/// Outcome of a successful authentication step: either a complete session or a challenge
/// for the next factor. Failures are returned as a failed <c>TaskResult</c>.
/// </summary>
public sealed class NhAuthenticationResult
{
    private NhAuthenticationResult(UserToken? session, NhTwoFactorChallengeResponse? challenge)
    {
        Session = session;
        Challenge = challenge;
    }

    /// <summary>The issued session when every required factor was satisfied.</summary>
    public UserToken? Session { get; }

    /// <summary>The pending second-factor challenge when another factor is required.</summary>
    public NhTwoFactorChallengeResponse? Challenge { get; }

    /// <summary>Whether a complete session was issued.</summary>
    public bool IsAuthenticated => Session != null;

    public static NhAuthenticationResult Authenticated(UserToken session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return new NhAuthenticationResult(session, null);
    }

    public static NhAuthenticationResult TwoFactorRequired(NhTwoFactorChallengeResponse challenge)
    {
        ArgumentNullException.ThrowIfNull(challenge);
        return new NhAuthenticationResult(null, challenge);
    }
}

/// <summary>
/// Login response used when two-factor authentication is enabled. A complete session has
/// the same properties as <see cref="UserToken"/>; a pending step only has
/// <see cref="TwoFactor"/>.
/// </summary>
public sealed class NhLoginResponse
{
    public string? Token { get; init; }
    public DateTime? ValidTo { get; init; }
    public string? RefreshToken { get; init; }
    public DateTime? RefreshValidTo { get; init; }
    public string? Issuer { get; init; }

    /// <summary>The second-factor challenge, or <see langword="null"/> for a complete session.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public NhTwoFactorChallengeResponse? TwoFactor { get; init; }

    public static NhLoginResponse FromSession(UserToken session)
    {
        ArgumentNullException.ThrowIfNull(session);

        return new NhLoginResponse
        {
            Token = session.Token,
            ValidTo = session.ValidTo,
            RefreshToken = session.RefreshToken,
            RefreshValidTo = session.RefreshValidTo,
            Issuer = session.Issuer,
        };
    }

    public static NhLoginResponse FromChallenge(NhTwoFactorChallengeResponse challenge)
    {
        ArgumentNullException.ThrowIfNull(challenge);
        return new NhLoginResponse { TwoFactor = challenge };
    }
}

/// <summary>
/// Completes a pending second-factor challenge.
/// </summary>
public sealed class NhTwoFactorVerifyRequest
{
    [Required]
    public string ChallengeToken { get; init; } = string.Empty;

    /// <summary>See <see cref="NhTwoFactorMethods"/>.</summary>
    [Required]
    public string Method { get; init; } = string.Empty;

    [Required]
    public string Code { get; init; } = string.Empty;
}

/// <summary>
/// Proof that the signed-in user is present before a sensitive account change. Provide the
/// current password or a valid second-factor code.
/// </summary>
public sealed class NhTwoFactorReauthentication
{
    public string? Password { get; init; }

    /// <summary>Second-factor method for <see cref="Code"/>, see <see cref="NhTwoFactorMethods"/>.</summary>
    public string? Method { get; init; }

    public string? Code { get; init; }
}

/// <summary>
/// Request for a sensitive two-factor change that needs reauthentication.
/// </summary>
public sealed class NhTwoFactorReauthenticationRequest
{
    public NhTwoFactorReauthentication? Reauthentication { get; init; }
}

/// <summary>
/// Confirms a pending authenticator setup with a code from the authenticator app.
/// </summary>
public sealed class NhAuthenticatorConfirmRequest
{
    [Required]
    public string Code { get; init; } = string.Empty;
}

/// <summary>
/// Two-factor state of the signed-in user.
/// </summary>
public sealed class NhTwoFactorStatusViewModel
{
    public bool Enabled { get; init; }

    /// <summary>Whether the two-factor policy requires a second factor for this user.</summary>
    public bool Required { get; init; }

    /// <summary>Enrolled methods, see <see cref="NhTwoFactorMethods"/>.</summary>
    public IReadOnlyList<string> Methods { get; init; } = [];

    /// <summary>Methods the application offers for enrollment.</summary>
    public IReadOnlyList<string> AvailableMethods { get; init; } = [];

    public int RecoveryCodesLeft { get; init; }

    public bool AuthenticatorSetupPending { get; init; }
}

/// <summary>
/// Pending authenticator setup. The key is shown once and becomes active after confirmation.
/// </summary>
public sealed class NhAuthenticatorSetupViewModel
{
    /// <summary>The shared key formatted in groups for manual entry.</summary>
    public required string SharedKey { get; init; }

    /// <summary>The <c>otpauth://</c> URI that authenticator apps import.</summary>
    public required string AuthenticatorUri { get; init; }

    /// <summary>PNG data URI of the QR code for <see cref="AuthenticatorUri"/>.</summary>
    public string? QrCodeDataUri { get; init; }
}

/// <summary>
/// Result of a two-factor change. Recovery codes are only returned once. When the change
/// ended every other session, <see cref="Session"/> holds a new session for this device.
/// </summary>
public sealed class NhTwoFactorChangeResponse
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? RecoveryCodes { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public UserToken? Session { get; init; }
}
