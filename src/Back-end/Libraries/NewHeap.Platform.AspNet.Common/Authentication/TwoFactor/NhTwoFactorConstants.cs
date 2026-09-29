using NewHeap.Platform.Common.Models;

namespace NewHeap.Platform.AspNet.Common.Authentication.TwoFactor;

/// <summary>
/// Second-factor methods that a user can present after the first factor succeeded.
/// </summary>
public static class NhTwoFactorMethods
{
    /// <summary>A time-based one-time password from an authenticator app.</summary>
    public const string Authenticator = "authenticator";

    /// <summary>A single-use recovery code issued when two-factor authentication was enabled.</summary>
    public const string RecoveryCode = "recovery-code";

    /// <summary>A single-use code sent to the user's confirmed e-mail address.</summary>
    public const string Email = "email";

    /// <summary>A WebAuthn passkey with user verification.</summary>
    public const string Passkey = "passkey";

    /// <summary>Whether the method proves possession on its own, unlike a recovery code.</summary>
    public static bool IsPrimary(string method)
    {
        return method != RecoveryCode;
    }
}

/// <summary>
/// First factors that can start an authentication session.
/// </summary>
public static class NhAuthenticationFactors
{
    /// <summary>Username and password.</summary>
    public const string Password = "password";

    /// <summary>A Microsoft OAuth sign-in. The identity provider owns its own MFA policy.</summary>
    public const string MicrosoftOAuth = "microsoft-oauth";

    /// <summary>A passwordless sign-in with a passkey that verified the user.</summary>
    public const string Passkey = "passkey";

    /// <summary>A refresh token that rotates an existing session.</summary>
    public const string RefreshToken = "refresh-token";

    /// <summary>A trusted server-side sign-in through <c>LoginWithoutValidations</c>.</summary>
    public const string Trusted = "trusted";

    private const string CustomPrefix = "custom:";

    /// <summary>
    /// Creates the factor name for a consumer-specific credential such as a PIN.
    /// </summary>
    /// <param name="name">Lowercase dash-case credential name.</param>
    /// <returns>The factor name passed to the two-factor policy.</returns>
    public static string Custom(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return CustomPrefix + name.Trim();
    }

    /// <summary>
    /// Returns whether the factor is a consumer-specific credential.
    /// </summary>
    public static bool IsCustom(string factor)
    {
        return factor.StartsWith(CustomPrefix, StringComparison.Ordinal);
    }
}

/// <summary>
/// Status values returned when an authentication step needs another action.
/// </summary>
public static class NhAuthenticationStepStatuses
{
    /// <summary>The user must present a second factor.</summary>
    public const string TwoFactorRequired = "two-factor-required";

    /// <summary>The policy requires a second factor that the user has not enrolled yet.</summary>
    public const string EnrollmentRequired = "enrollment-required";
}

/// <summary>
/// Safe failure codes for two-factor outcomes. Each result item uses the code as its name
/// and the matching <see cref="MessageKey"/> as its message, so clients can localize it.
/// </summary>
public static class NhTwoFactorFailureCodes
{
    public const string Required = "two-factor-required";
    public const string EnrollmentRequired = "two-factor-enrollment-required";
    public const string InvalidCode = "two-factor-invalid-code";
    public const string ChallengeExpired = "two-factor-challenge-expired";
    public const string LockedOut = "two-factor-locked-out";
    public const string MethodNotAllowed = "two-factor-method-not-allowed";
    public const string NotEnabled = "two-factor-not-enabled";
    public const string AlreadyEnabled = "two-factor-already-enabled";
    public const string SetupNotStarted = "two-factor-setup-not-started";
    public const string ReauthenticationRequired = "two-factor-reauthentication-required";
    public const string ReauthenticationFailed = "two-factor-reauthentication-failed";
    public const string RequiredByPolicy = "two-factor-required-by-policy";
    public const string NotAllowedWhileImpersonating = "two-factor-not-allowed-while-impersonating";
    public const string ConfigurationInvalid = "two-factor-configuration-invalid";
    public const string EmailCooldown = "two-factor-email-cooldown";
    public const string EmailUnavailable = "two-factor-email-unavailable";
    public const string UserNotFound = "two-factor-user-not-found";
    public const string PasskeyInvalid = "two-factor-passkey-invalid";
    public const string PasskeyNotFound = "two-factor-passkey-not-found";

    private const string MessageKeyPrefix = "nh-two-factor.";
    private const string CodePrefix = "two-factor-";

    /// <summary>
    /// Returns the localization key for a failure code, for example
    /// <c>nh-two-factor.invalid-code</c> for <see cref="InvalidCode"/>.
    /// </summary>
    public static string MessageKey(string failureCode)
    {
        var suffix = failureCode.StartsWith(CodePrefix, StringComparison.Ordinal)
            ? failureCode[CodePrefix.Length..]
            : failureCode;

        return MessageKeyPrefix + suffix;
    }

    internal static TaskResult<T> Fail<T>(string failureCode)
    {
        var result = new TaskResult<T>();
        result.AddError(failureCode, MessageKey(failureCode));
        return result;
    }

    internal static TaskResult Fail(string failureCode)
    {
        var result = new TaskResult();
        result.AddError(failureCode, MessageKey(failureCode));
        return result;
    }

    internal static bool Has(TaskResult result, string failureCode)
    {
        return result.GetResultItems().Any(x => x.Name == failureCode);
    }
}
