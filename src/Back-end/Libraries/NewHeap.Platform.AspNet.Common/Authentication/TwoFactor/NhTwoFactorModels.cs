using NewHeap.Platform.AspNet.Common.Models;
using System.ComponentModel.DataAnnotations;
using System.Text.Json;
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

    /// <summary>Whether completing the challenge can remember this device for later sign-ins.</summary>
    public bool RememberDeviceAvailable { get; init; }
}

/// <summary>
/// Outcome of a successful authentication step: either a complete session or a challenge
/// for the next factor. Failures are returned as a failed <c>TaskResult</c>.
/// </summary>
public sealed class NhAuthenticationResult
{
    private NhAuthenticationResult(
        UserToken? session,
        NhTwoFactorChallengeResponse? challenge,
        string? rememberDeviceToken)
    {
        Session = session;
        Challenge = challenge;
        RememberDeviceToken = rememberDeviceToken;
    }

    /// <summary>The issued session when every required factor was satisfied.</summary>
    public UserToken? Session { get; }

    /// <summary>
    /// The pending step when another action is required: a second-factor challenge
    /// (<see cref="NhAuthenticationStepStatuses.TwoFactorRequired"/>) or an enrollment
    /// (<see cref="NhAuthenticationStepStatuses.EnrollmentRequired"/>).
    /// </summary>
    public NhTwoFactorChallengeResponse? Challenge { get; }

    /// <summary>A new remember-device token when the user chose to remember this device.</summary>
    public string? RememberDeviceToken { get; }

    /// <summary>Whether a complete session was issued.</summary>
    public bool IsAuthenticated => Session != null;

    public static NhAuthenticationResult Authenticated(UserToken session, string? rememberDeviceToken = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        return new NhAuthenticationResult(session, null, rememberDeviceToken);
    }

    public static NhAuthenticationResult Pending(NhTwoFactorChallengeResponse challenge)
    {
        ArgumentNullException.ThrowIfNull(challenge);
        return new NhAuthenticationResult(null, challenge, null);
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

    /// <summary>The pending step, or <see langword="null"/> for a complete session.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public NhTwoFactorChallengeResponse? TwoFactor { get; init; }

    /// <summary>
    /// A remember-device token for clients that send the <c>Authorization</c> header. Cookie
    /// clients receive the same token as an HttpOnly cookie.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RememberDeviceToken { get; init; }

    public static NhLoginResponse FromSession(UserToken session, string? rememberDeviceToken = null)
    {
        ArgumentNullException.ThrowIfNull(session);

        return new NhLoginResponse
        {
            Token = session.Token,
            ValidTo = session.ValidTo,
            RefreshToken = session.RefreshToken,
            RefreshValidTo = session.RefreshValidTo,
            Issuer = session.Issuer,
            RememberDeviceToken = rememberDeviceToken,
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

    /// <summary>Whether to skip the second factor on this device for later sign-ins.</summary>
    public bool RememberDevice { get; init; }
}

/// <summary>
/// Refers to a pending challenge, for example to send an e-mailed code.
/// </summary>
public sealed class NhTwoFactorChallengeRequest
{
    [Required]
    public string ChallengeToken { get; init; } = string.Empty;
}

/// <summary>
/// Refers to a pending enrollment of a user whom the policy requires to use a second factor.
/// </summary>
public sealed class NhTwoFactorEnrollmentRequest
{
    [Required]
    public string EnrollmentToken { get; init; } = string.Empty;
}

/// <summary>
/// Confirms a method during a pending enrollment.
/// </summary>
public sealed class NhTwoFactorEnrollmentConfirmRequest
{
    [Required]
    public string EnrollmentToken { get; init; } = string.Empty;

    [Required]
    public string Code { get; init; } = string.Empty;
}

/// <summary>
/// Confirmation that a code was e-mailed.
/// </summary>
public sealed class NhTwoFactorEmailCodeSentResponse
{
    public required DateTimeOffset ExpiresAt { get; init; }

    /// <summary>When another code can be requested.</summary>
    public required DateTimeOffset ResendAvailableAt { get; init; }
}

/// <summary>
/// Starts an administrative two-factor background operation.
/// </summary>
public sealed class NhTwoFactorOperationRequest
{
    /// <summary>Key that makes a repeated request return the operation that was already started.</summary>
    [Required]
    [StringLength(100)]
    public string IdempotencyKey { get; init; } = string.Empty;
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
/// Confirms a pending setup with a code from the authenticator app or e-mail.
/// </summary>
public sealed class NhTwoFactorCodeRequest
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

    /// <summary>Whether the user can remember a device to skip the second factor.</summary>
    public bool RememberDeviceAvailable { get; init; }

    /// <summary>Whether an e-mail factor confirmation is pending.</summary>
    public bool EmailSetupPending { get; init; }

    /// <summary>Enrolled methods, see <see cref="NhTwoFactorMethods"/>.</summary>
    public IReadOnlyList<string> Methods { get; init; } = [];

    /// <summary>Methods the application offers for enrollment.</summary>
    public IReadOnlyList<string> AvailableMethods { get; init; } = [];

    public int RecoveryCodesLeft { get; init; }

    public bool AuthenticatorSetupPending { get; init; }

    /// <summary>Number of registered passkeys.</summary>
    public int PasskeyCount { get; init; }
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

/// <summary>
/// WebAuthn options for a passkey ceremony. Pass <see cref="Options"/> to
/// <c>PublicKeyCredential.parseCreationOptionsFromJSON</c> or
/// <c>parseRequestOptionsFromJSON</c> and send the result back with <see cref="CeremonyToken"/>.
/// </summary>
public sealed class NhPasskeyOptionsResponse
{
    /// <summary>The WebAuthn creation or request options in their JSON form.</summary>
    public required JsonElement Options { get; init; }

    /// <summary>Opaque, short-lived token that carries the ceremony state.</summary>
    public required string CeremonyToken { get; init; }

    public required DateTimeOffset ExpiresAt { get; init; }
}

/// <summary>
/// Registers a passkey for the signed-in user.
/// </summary>
public sealed class NhPasskeyRegistrationRequest
{
    [Required]
    public string CeremonyToken { get; init; } = string.Empty;

    /// <summary>The <c>PublicKeyCredential</c> in its JSON form (<c>credential.toJSON()</c>).</summary>
    [Required]
    public JsonElement Credential { get; init; }

    /// <summary>A name that helps the user recognize the passkey later.</summary>
    [MaxLength(NhPasskeyViewModel.MaxNameLength)]
    public string? Name { get; init; }
}

/// <summary>
/// Registers a passkey during a pending enrollment.
/// </summary>
public sealed class NhPasskeyEnrollmentRequest
{
    [Required]
    public string EnrollmentToken { get; init; } = string.Empty;

    [Required]
    public string CeremonyToken { get; init; } = string.Empty;

    /// <summary>The <c>PublicKeyCredential</c> in its JSON form (<c>credential.toJSON()</c>).</summary>
    [Required]
    public JsonElement Credential { get; init; }

    [MaxLength(NhPasskeyViewModel.MaxNameLength)]
    public string? Name { get; init; }
}

/// <summary>
/// Completes a pending second-factor challenge with a passkey.
/// </summary>
public sealed class NhTwoFactorPasskeyVerifyRequest
{
    [Required]
    public string ChallengeToken { get; init; } = string.Empty;

    [Required]
    public string CeremonyToken { get; init; } = string.Empty;

    /// <summary>The <c>PublicKeyCredential</c> in its JSON form (<c>credential.toJSON()</c>).</summary>
    [Required]
    public JsonElement Credential { get; init; }

    /// <summary>Whether to skip the second factor on this device for later sign-ins.</summary>
    public bool RememberDevice { get; init; }
}

/// <summary>
/// Signs in without a password with a discoverable passkey.
/// </summary>
public sealed class NhPasskeySignInRequest
{
    [Required]
    public string CeremonyToken { get; init; } = string.Empty;

    /// <summary>The <c>PublicKeyCredential</c> in its JSON form (<c>credential.toJSON()</c>).</summary>
    [Required]
    public JsonElement Credential { get; init; }

    /// <summary>A remember-device token for clients that send the <c>Authorization</c> header.</summary>
    public string? RememberDeviceToken { get; init; }
}

/// <summary>
/// Renames a passkey of the signed-in user.
/// </summary>
public sealed class NhPasskeyRenameRequest
{
    [Required]
    [MaxLength(NhPasskeyViewModel.MaxNameLength)]
    public string Name { get; init; } = string.Empty;
}

/// <summary>
/// A registered passkey. The public key and attestation data are never returned.
/// </summary>
public sealed class NhPasskeyViewModel
{
    internal const int MaxNameLength = 100;

    /// <summary>The credential ID, base64url encoded.</summary>
    public required string Id { get; init; }

    public required string Name { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>Whether the passkey is synced by a passkey provider, such as a password manager.</summary>
    public bool IsBackedUp { get; init; }

    /// <summary>Transport hints reported by the authenticator, such as <c>internal</c> or <c>usb</c>.</summary>
    public IReadOnlyList<string> Transports { get; init; } = [];
}
