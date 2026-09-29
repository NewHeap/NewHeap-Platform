using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NewHeap.Platform.AspNet.Common.DAL.Entities;
using NewHeap.Platform.AspNet.Common.Services;
using NewHeap.Platform.AspNet.Common.Services.Notification;
using NewHeap.Platform.Common.Models;
using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;

namespace NewHeap.Platform.AspNet.Common.Authentication.TwoFactor;

/// <summary>
/// Account operations for two-factor authentication of the signed-in user.
/// </summary>
public interface INhTwoFactorService<TUser>
    where TUser : IdentityUser<Guid>
{
    /// <summary>Returns the user's enrolled methods.</summary>
    Task<NhTwoFactorEnrollment> GetEnrollmentAsync(TUser user, CancellationToken cancellationToken = default);

    /// <summary>Evaluates the two-factor policy for a first factor.</summary>
    Task<NhTwoFactorRequirement> EvaluatePolicyAsync(TUser user, string factor, CancellationToken cancellationToken = default);

    Task<NhTwoFactorStatusViewModel> GetStatusAsync(TUser user, CancellationToken cancellationToken = default);

    /// <summary>
    /// Starts authenticator enrollment with a new pending key. The active key, if any,
    /// stays in use until the new key is confirmed. Restarting enrollment while two-factor
    /// authentication is enabled requires reauthentication.
    /// </summary>
    Task<TaskResult<NhAuthenticatorSetupViewModel>> BeginAuthenticatorSetupAsync(
        TUser user,
        NhTwoFactorReauthentication? reauthentication,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Activates the pending authenticator key, enables two-factor authentication, issues
    /// recovery codes and ends every session of the user.
    /// </summary>
    Task<TaskResult<NhTwoFactorChangeResult>> ConfirmAuthenticatorAsync(
        TUser user,
        string code,
        Guid? committedByUserId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// E-mails a code that confirms the user's e-mail address as a second factor. Requires
    /// reauthentication while two-factor authentication is enabled.
    /// </summary>
    Task<TaskResult<NhTwoFactorEmailCodeSentResponse>> BeginEmailSetupAsync(
        TUser user,
        NhTwoFactorReauthentication? reauthentication,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Confirms the e-mail factor, enables two-factor authentication and ends every session
    /// of the user.
    /// </summary>
    Task<TaskResult<NhTwoFactorChangeResult>> ConfirmEmailSetupAsync(
        TUser user,
        string code,
        Guid? committedByUserId,
        CancellationToken cancellationToken = default);

    /// <summary>Replaces the user's recovery codes. Requires reauthentication.</summary>
    Task<TaskResult<NhTwoFactorChangeResult>> RegenerateRecoveryCodesAsync(
        TUser user,
        NhTwoFactorReauthentication? reauthentication,
        Guid? committedByUserId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Disables two-factor authentication and ends every session of the user. Requires
    /// reauthentication and fails when the policy requires a second factor.
    /// </summary>
    Task<TaskResult<NhTwoFactorChangeResult>> DisableAsync(
        TUser user,
        NhTwoFactorReauthentication? reauthentication,
        Guid? committedByUserId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Forgets every remembered device and ends every session of the user. Requires
    /// reauthentication.
    /// </summary>
    Task<TaskResult<NhTwoFactorChangeResult>> ForgetDevicesAsync(
        TUser user,
        NhTwoFactorReauthentication? reauthentication,
        Guid? committedByUserId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes every second factor of a user who lost access to them and ends every session.
    /// A user whom the policy requires to use a second factor enrolls again at the next sign-in.
    /// </summary>
    Task<TaskResult> ResetAsync(
        TUser user,
        Guid? committedByUserId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Verifies a second-factor code outside a sign-in, for example as reauthentication.
    /// A wrong code counts as a failed access attempt.
    /// </summary>
    Task<TaskResult> VerifySecondFactorAsync(
        TUser user,
        string method,
        string code,
        CancellationToken cancellationToken = default);

    /// <summary>Returns the user's registered passkeys.</summary>
    Task<IReadOnlyList<NhPasskeyViewModel>> GetPasskeysAsync(TUser user, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates WebAuthn creation options for a new passkey. Requires reauthentication while
    /// two-factor authentication is enabled.
    /// </summary>
    Task<TaskResult<NhPasskeyOptionsResponse>> BeginPasskeyRegistrationAsync(
        TUser user,
        NhTwoFactorReauthentication? reauthentication,
        HttpContext httpContext,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Verifies the authenticator's attestation, stores the passkey, enables two-factor
    /// authentication and ends every session of the user.
    /// </summary>
    Task<TaskResult<NhTwoFactorChangeResult>> CompletePasskeyRegistrationAsync(
        TUser user,
        string ceremonyToken,
        string credentialJson,
        string? name,
        HttpContext httpContext,
        Guid? committedByUserId,
        CancellationToken cancellationToken = default);

    /// <summary>Renames a passkey.</summary>
    Task<TaskResult> RenamePasskeyAsync(
        TUser user,
        string passkeyId,
        string name,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes a passkey and ends every session of the user. Requires reauthentication.
    /// Removing the last second factor disables two-factor authentication, which fails when
    /// the policy requires a second factor.
    /// </summary>
    Task<TaskResult<NhTwoFactorChangeResult>> RemovePasskeyAsync(
        TUser user,
        string passkeyId,
        NhTwoFactorReauthentication? reauthentication,
        Guid? committedByUserId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// The two-factor methods a user has enrolled.
/// </summary>
public sealed class NhTwoFactorEnrollment
{
    internal NhTwoFactorEnrollment(
        bool isEnrolled,
        IReadOnlyList<string> methods,
        int recoveryCodesLeft,
        bool authenticatorSetupPending,
        bool emailSetupPending,
        int passkeyCount = 0)
    {
        IsEnrolled = isEnrolled;
        Methods = methods;
        RecoveryCodesLeft = recoveryCodesLeft;
        AuthenticatorSetupPending = authenticatorSetupPending;
        EmailSetupPending = emailSetupPending;
        PasskeyCount = passkeyCount;
    }

    public bool IsEnrolled { get; }
    public IReadOnlyList<string> Methods { get; }
    public int RecoveryCodesLeft { get; }
    public bool AuthenticatorSetupPending { get; }
    public bool EmailSetupPending { get; }
    public int PasskeyCount { get; }
}

/// <summary>
/// Outcome of a two-factor change.
/// </summary>
public sealed class NhTwoFactorChangeResult
{
    internal NhTwoFactorChangeResult(IReadOnlyList<string>? recoveryCodes, NhAuthenticationProof? renewalProof)
    {
        RecoveryCodes = recoveryCodes;
        RenewalProof = renewalProof;
    }

    /// <summary>New recovery codes to show once, if the change issued them.</summary>
    public IReadOnlyList<string>? RecoveryCodes { get; }

    /// <summary>Whether the change ended every session of the user.</summary>
    public bool SessionsInvalidated => RenewalProof != null;

    /// <summary>Proof that allows a new session for the device that made the change.</summary>
    public NhAuthenticationProof? RenewalProof { get; }
}

/// <summary>
/// Default two-factor implementation built on ASP.NET Core Identity storage. Keys, recovery
/// codes, e-mail codes and replay state live in the Identity user-token table, so no schema
/// change is needed. Passkeys use the Identity passkey table, see
/// <c>NhIdentityDbContext.IncludeIdentityPasskeys</c>.
/// </summary>
public partial class NhTwoFactorService<TUser> : INhTwoFactorService<TUser>
    where TUser : IdentityUser<Guid>
{
    private const string LoginProvider = NhAuthenticationSessionDefaults.LoginProvider;
    private const string PendingAuthenticatorKeyTokenName = "TwoFactorPendingAuthenticatorKey";
    private const string LastAuthenticatorStepTokenName = "TwoFactorLastAuthenticatorStep";
    private const string ConsumedChallengesTokenName = "TwoFactorConsumedChallenges";
    private const string EmailFactorTokenName = "TwoFactorEmailFactor";
    private const string EmailCodeTokenName = "TwoFactorEmailCode";

    private readonly UserManager<TUser> _userManager;
    private readonly INhUserManager<TUser> _nhUserManager;
    private readonly IUserStore<TUser> _userStore;
    private readonly INhDbLogService _dbLogService;
    private readonly NhTwoFactorConfiguration _configuration;
    private readonly INhTwoFactorPolicy _policy;
    private readonly NhTwoFactorTicketProtector _ticketProtector;
    private readonly INhQrCodeRenderer _qrCodeRenderer;
    private readonly IHostEnvironment _hostEnvironment;
    private readonly TimeProvider _timeProvider;
    private readonly INhTwoFactorMessageComposer _messageComposer;
    private readonly INhNotificationService? _notificationService;
    private readonly ILogger<NhTwoFactorService<TUser>> _logger;
    private readonly IPasskeyHandler<TUser>? _passkeyHandler;

    internal NhTwoFactorService(
        UserManager<TUser> userManager,
        INhUserManager<TUser> nhUserManager,
        IUserStore<TUser> userStore,
        INhDbLogService dbLogService,
        NhTwoFactorConfiguration configuration,
        INhTwoFactorPolicy policy,
        NhTwoFactorTicketProtector ticketProtector,
        INhQrCodeRenderer qrCodeRenderer,
        IHostEnvironment hostEnvironment,
        TimeProvider timeProvider,
        INhTwoFactorMessageComposer messageComposer,
        INhNotificationService? notificationService,
        ILogger<NhTwoFactorService<TUser>> logger,
        IPasskeyHandler<TUser>? passkeyHandler = null)
    {
        _userManager = userManager;
        _nhUserManager = nhUserManager;
        _userStore = userStore;
        _dbLogService = dbLogService;
        _configuration = configuration;
        _policy = policy;
        _ticketProtector = ticketProtector;
        _qrCodeRenderer = qrCodeRenderer;
        _hostEnvironment = hostEnvironment;
        _timeProvider = timeProvider;
        _messageComposer = messageComposer;
        _notificationService = notificationService;
        _logger = logger;
        _passkeyHandler = passkeyHandler;
    }

    internal NhTwoFactorConfiguration Configuration => _configuration;

    public virtual async Task<NhTwoFactorEnrollment> GetEnrollmentAsync(
        TUser user,
        CancellationToken cancellationToken = default)
    {
        var enabled = await _userManager.GetTwoFactorEnabledAsync(user);
        var hasAuthenticator = _configuration.AuthenticatorEnabled
            && !string.IsNullOrEmpty(await _userManager.GetAuthenticatorKeyAsync(user));
        var hasEmail = _configuration.EmailCodesEnabled
            && await HasConfirmedEmailFactorAsync(user);
        var passkeyCount = await CountPasskeysAsync(user);

        var recoveryCodesLeft = 0;
        if (_configuration.RecoveryCodesEnabled)
        {
            recoveryCodesLeft = await _userManager.CountRecoveryCodesAsync(user);
        }

        var pendingKey = await _userManager.GetAuthenticationTokenAsync(
            user,
            LoginProvider,
            PendingAuthenticatorKeyTokenName);
        var pendingEmailCode = ReadEmailCode(await _userManager.GetAuthenticationTokenAsync(user, LoginProvider, EmailCodeTokenName));
        var emailSetupPending = pendingEmailCode?.Purpose == NhTwoFactorEmailCodePurpose.Confirmation
            && pendingEmailCode.ExpiresAt > _timeProvider.GetUtcNow();

        var methods = new List<string>();
        if (enabled && hasAuthenticator)
        {
            methods.Add(NhTwoFactorMethods.Authenticator);
        }

        if (enabled && hasEmail)
        {
            methods.Add(NhTwoFactorMethods.Email);
        }

        if (enabled && passkeyCount > 0)
        {
            methods.Add(NhTwoFactorMethods.Passkey);
        }

        var isEnrolled = methods.Count > 0;
        if (isEnrolled && recoveryCodesLeft > 0)
        {
            methods.Add(NhTwoFactorMethods.RecoveryCode);
        }

        return new NhTwoFactorEnrollment(
            isEnrolled,
            methods,
            recoveryCodesLeft,
            !string.IsNullOrEmpty(pendingKey),
            emailSetupPending,
            passkeyCount);
    }

    public virtual async Task<NhTwoFactorRequirement> EvaluatePolicyAsync(
        TUser user,
        string factor,
        CancellationToken cancellationToken = default)
    {
        var evaluation = await EvaluateAsync(user, factor, cancellationToken);
        return evaluation.Requirement;
    }

    public virtual async Task<NhTwoFactorStatusViewModel> GetStatusAsync(
        TUser user,
        CancellationToken cancellationToken = default)
    {
        var evaluation = await EvaluateAsync(user, NhAuthenticationFactors.Password, cancellationToken);
        var enrollment = evaluation.Enrollment;

        return new NhTwoFactorStatusViewModel
        {
            Enabled = enrollment.IsEnrolled,
            Required = evaluation.Requirement.EnforcedByPolicy,
            Methods = enrollment.Methods,
            AvailableMethods = _configuration.AvailableMethods,
            RecoveryCodesLeft = enrollment.RecoveryCodesLeft,
            AuthenticatorSetupPending = enrollment.AuthenticatorSetupPending,
            EmailSetupPending = enrollment.EmailSetupPending,
            PasskeyCount = enrollment.PasskeyCount,
            RememberDeviceAvailable = _configuration.RememberDeviceEnabled && evaluation.Requirement.AllowRememberDevice,
        };
    }

    public virtual async Task<TaskResult<NhAuthenticatorSetupViewModel>> BeginAuthenticatorSetupAsync(
        TUser user,
        NhTwoFactorReauthentication? reauthentication,
        CancellationToken cancellationToken = default)
    {
        var enrollment = await GetEnrollmentAsync(user, cancellationToken);
        if (enrollment.IsEnrolled)
        {
            var reauthenticationResult = await VerifyReauthenticationAsync(user, reauthentication, cancellationToken);
            if (!reauthenticationResult.Success)
            {
                return TaskResult<NhAuthenticatorSetupViewModel>.Failed(reauthenticationResult);
            }
        }

        return await BeginAuthenticatorSetupCoreAsync(user);
    }

    public virtual async Task<TaskResult<NhTwoFactorChangeResult>> ConfirmAuthenticatorAsync(
        TUser user,
        string code,
        Guid? committedByUserId,
        CancellationToken cancellationToken = default)
    {
        var pendingKey = await _userManager.GetAuthenticationTokenAsync(
            user,
            LoginProvider,
            PendingAuthenticatorKeyTokenName);

        if (string.IsNullOrEmpty(pendingKey))
        {
            return NhTwoFactorFailureCodes.Fail<NhTwoFactorChangeResult>(NhTwoFactorFailureCodes.SetupNotStarted);
        }

        var matchedStep = NhTotp.Match(pendingKey, code, _timeProvider.GetUtcNow(), lastAcceptedStep: null);
        if (!matchedStep.HasValue)
        {
            return NhTwoFactorFailureCodes.Fail<NhTwoFactorChangeResult>(NhTwoFactorFailureCodes.InvalidCode);
        }

        if (_userStore is not IUserAuthenticatorKeyStore<TUser> authenticatorKeyStore)
        {
            throw new NotSupportedException("The configured user store does not support authenticator keys.");
        }

        var wasEnrolled = (await GetEnrollmentAsync(user, cancellationToken)).IsEnrolled;
        IReadOnlyList<string>? recoveryCodes = null;

        var mutationResult = await ExecuteSecurityMutationAsync(
            user,
            async () =>
            {
                await authenticatorKeyStore.SetAuthenticatorKeyAsync(user, pendingKey, cancellationToken);

                var removePendingResult = await _userManager.RemoveAuthenticationTokenAsync(
                    user,
                    LoginProvider,
                    PendingAuthenticatorKeyTokenName);
                if (!removePendingResult.Succeeded)
                {
                    return IdentityFailure(removePendingResult);
                }

                var stepResult = await SetLastAuthenticatorStepAsync(user, matchedStep.Value);
                if (!stepResult.Succeeded)
                {
                    return IdentityFailure(stepResult);
                }

                var enableResult = await _userManager.SetTwoFactorEnabledAsync(user, true);
                if (!enableResult.Succeeded)
                {
                    return IdentityFailure(enableResult);
                }

                var recoveryCodesResult = await EnsureRecoveryCodesAsync(user, replaceExisting: !wasEnrolled);
                if (!recoveryCodesResult.Success)
                {
                    return recoveryCodesResult;
                }

                recoveryCodes = recoveryCodesResult.Data;

                return await NotifyAsync(
                    user,
                    wasEnrolled ? NhTwoFactorSecurityEvent.AuthenticatorChanged : NhTwoFactorSecurityEvent.Enabled,
                    cancellationToken: cancellationToken);
            },
            wasEnrolled ? "Two-factor authenticator changed." : "Two-factor authentication enabled.",
            committedByUserId,
            cancellationToken);

        if (!mutationResult.Success)
        {
            return TaskResult<NhTwoFactorChangeResult>.Failed(mutationResult);
        }

        return new NhTwoFactorChangeResult(recoveryCodes, new NhAuthenticationProof(user.Id, [NhTwoFactorMethods.Authenticator]));
    }

    public virtual async Task<TaskResult<NhTwoFactorEmailCodeSentResponse>> BeginEmailSetupAsync(
        TUser user,
        NhTwoFactorReauthentication? reauthentication,
        CancellationToken cancellationToken = default)
    {
        if (!_configuration.EmailCodesEnabled)
        {
            return NhTwoFactorFailureCodes.Fail<NhTwoFactorEmailCodeSentResponse>(NhTwoFactorFailureCodes.MethodNotAllowed);
        }

        var enrollment = await GetEnrollmentAsync(user, cancellationToken);
        if (enrollment.IsEnrolled)
        {
            var reauthenticationResult = await VerifyReauthenticationAsync(user, reauthentication, cancellationToken);
            if (!reauthenticationResult.Success)
            {
                return TaskResult<NhTwoFactorEmailCodeSentResponse>.Failed(reauthenticationResult);
            }
        }

        return await SendEmailCodeAsync(user, NhTwoFactorEmailCodePurpose.Confirmation, cancellationToken);
    }

    public virtual async Task<TaskResult<NhTwoFactorChangeResult>> ConfirmEmailSetupAsync(
        TUser user,
        string code,
        Guid? committedByUserId,
        CancellationToken cancellationToken = default)
    {
        if (!_configuration.EmailCodesEnabled)
        {
            return NhTwoFactorFailureCodes.Fail<NhTwoFactorChangeResult>(NhTwoFactorFailureCodes.MethodNotAllowed);
        }

        var storedCode = ReadEmailCode(await _userManager.GetAuthenticationTokenAsync(user, LoginProvider, EmailCodeTokenName));
        if (storedCode == null || storedCode.Purpose != NhTwoFactorEmailCodePurpose.Confirmation)
        {
            return NhTwoFactorFailureCodes.Fail<NhTwoFactorChangeResult>(NhTwoFactorFailureCodes.SetupNotStarted);
        }

        if (!MatchesEmailCode(user, storedCode, code))
        {
            return NhTwoFactorFailureCodes.Fail<NhTwoFactorChangeResult>(NhTwoFactorFailureCodes.InvalidCode);
        }

        var wasEnrolled = (await GetEnrollmentAsync(user, cancellationToken)).IsEnrolled;
        IReadOnlyList<string>? recoveryCodes = null;

        var mutationResult = await ExecuteSecurityMutationAsync(
            user,
            async () =>
            {
                var removeCodeResult = await _userManager.RemoveAuthenticationTokenAsync(user, LoginProvider, EmailCodeTokenName);
                if (!removeCodeResult.Succeeded)
                {
                    return IdentityFailure(removeCodeResult);
                }

                var emailFactorResult = await _userManager.SetAuthenticationTokenAsync(
                    user,
                    LoginProvider,
                    EmailFactorTokenName,
                    NormalizeEmail(await _userManager.GetEmailAsync(user)));
                if (!emailFactorResult.Succeeded)
                {
                    return IdentityFailure(emailFactorResult);
                }

                var enableResult = await _userManager.SetTwoFactorEnabledAsync(user, true);
                if (!enableResult.Succeeded)
                {
                    return IdentityFailure(enableResult);
                }

                var recoveryCodesResult = await EnsureRecoveryCodesAsync(user, replaceExisting: !wasEnrolled);
                if (!recoveryCodesResult.Success)
                {
                    return recoveryCodesResult;
                }

                recoveryCodes = recoveryCodesResult.Data;

                return await NotifyAsync(
                    user,
                    wasEnrolled ? NhTwoFactorSecurityEvent.EmailFactorEnabled : NhTwoFactorSecurityEvent.Enabled,
                    cancellationToken: cancellationToken);
            },
            "Two-factor e-mail factor enabled.",
            committedByUserId,
            cancellationToken);

        if (!mutationResult.Success)
        {
            return TaskResult<NhTwoFactorChangeResult>.Failed(mutationResult);
        }

        return new NhTwoFactorChangeResult(recoveryCodes, new NhAuthenticationProof(user.Id, [NhTwoFactorMethods.Email]));
    }

    public virtual async Task<TaskResult<NhTwoFactorChangeResult>> RegenerateRecoveryCodesAsync(
        TUser user,
        NhTwoFactorReauthentication? reauthentication,
        Guid? committedByUserId,
        CancellationToken cancellationToken = default)
    {
        if (!_configuration.RecoveryCodesEnabled)
        {
            return NhTwoFactorFailureCodes.Fail<NhTwoFactorChangeResult>(NhTwoFactorFailureCodes.MethodNotAllowed);
        }

        var enrollment = await GetEnrollmentAsync(user, cancellationToken);
        if (!enrollment.IsEnrolled)
        {
            return NhTwoFactorFailureCodes.Fail<NhTwoFactorChangeResult>(NhTwoFactorFailureCodes.NotEnabled);
        }

        var reauthenticationResult = await VerifyReauthenticationAsync(user, reauthentication, cancellationToken);
        if (!reauthenticationResult.Success)
        {
            return TaskResult<NhTwoFactorChangeResult>.Failed(reauthenticationResult);
        }

        var repository = _nhUserManager.GetRepository();
        await using var transaction = await repository.StartOrGetTransactionScopeAsync(cancellationToken);

        var recoveryCodes = await _userManager.GenerateNewTwoFactorRecoveryCodesAsync(
            user,
            _configuration.RecoveryCodeCount);
        if (recoveryCodes == null)
        {
            throw new InvalidOperationException("Could not store the two-factor recovery codes.");
        }

        var notificationResult = await NotifyAsync(user, NhTwoFactorSecurityEvent.RecoveryCodesRegenerated, cancellationToken: cancellationToken);
        if (!notificationResult.Success)
        {
            await transaction.RollbackAsync(cancellationToken);
            return TaskResult<NhTwoFactorChangeResult>.Failed(notificationResult);
        }

        await LogAsync(user, "Two-factor recovery codes regenerated.", committedByUserId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new NhTwoFactorChangeResult(recoveryCodes.ToList(), renewalProof: null);
    }

    public virtual async Task<TaskResult<NhTwoFactorChangeResult>> DisableAsync(
        TUser user,
        NhTwoFactorReauthentication? reauthentication,
        Guid? committedByUserId,
        CancellationToken cancellationToken = default)
    {
        var evaluation = await EvaluateAsync(user, NhAuthenticationFactors.Password, cancellationToken);
        if (!evaluation.Enrollment.IsEnrolled)
        {
            return NhTwoFactorFailureCodes.Fail<NhTwoFactorChangeResult>(NhTwoFactorFailureCodes.NotEnabled);
        }

        if (evaluation.Requirement.EnforcedByPolicy)
        {
            return NhTwoFactorFailureCodes.Fail<NhTwoFactorChangeResult>(NhTwoFactorFailureCodes.RequiredByPolicy);
        }

        var reauthenticationResult = await VerifyReauthenticationAsync(user, reauthentication, cancellationToken);
        if (!reauthenticationResult.Success)
        {
            return TaskResult<NhTwoFactorChangeResult>.Failed(reauthenticationResult);
        }

        var mutationResult = await ExecuteSecurityMutationAsync(
            user,
            async () =>
            {
                var removeResult = await RemoveSecondFactorsAsync(user, cancellationToken);
                if (!removeResult.Success)
                {
                    return removeResult;
                }

                return await NotifyAsync(user, NhTwoFactorSecurityEvent.Disabled, cancellationToken: cancellationToken);
            },
            "Two-factor authentication disabled.",
            committedByUserId,
            cancellationToken);

        if (!mutationResult.Success)
        {
            return TaskResult<NhTwoFactorChangeResult>.Failed(mutationResult);
        }

        return new NhTwoFactorChangeResult(null, new NhAuthenticationProof(user.Id, [NhAuthenticationFactors.Password]));
    }

    public virtual async Task<TaskResult<NhTwoFactorChangeResult>> ForgetDevicesAsync(
        TUser user,
        NhTwoFactorReauthentication? reauthentication,
        Guid? committedByUserId,
        CancellationToken cancellationToken = default)
    {
        var reauthenticationResult = await VerifyReauthenticationAsync(user, reauthentication, cancellationToken);
        if (!reauthenticationResult.Success)
        {
            return TaskResult<NhTwoFactorChangeResult>.Failed(reauthenticationResult);
        }

        var mutationResult = await ExecuteSecurityMutationAsync(
            user,
            async () =>
            {
                // Remember-device tokens carry the security stamp, so a new stamp forgets them all.
                var stampResult = await _userManager.UpdateSecurityStampAsync(user);
                if (!stampResult.Succeeded)
                {
                    return IdentityFailure(stampResult);
                }

                return await NotifyAsync(user, NhTwoFactorSecurityEvent.DevicesForgotten, cancellationToken: cancellationToken);
            },
            "Two-factor remembered devices forgotten.",
            committedByUserId,
            cancellationToken);

        if (!mutationResult.Success)
        {
            return TaskResult<NhTwoFactorChangeResult>.Failed(mutationResult);
        }

        return new NhTwoFactorChangeResult(null, new NhAuthenticationProof(user.Id, [NhAuthenticationFactors.Password]));
    }

    public virtual async Task<TaskResult> ResetAsync(
        TUser user,
        Guid? committedByUserId,
        CancellationToken cancellationToken = default)
    {
        return await ExecuteSecurityMutationAsync(
            user,
            async () =>
            {
                var removeResult = await RemoveSecondFactorsAsync(user, cancellationToken);
                if (!removeResult.Success)
                {
                    return removeResult;
                }

                return await NotifyAsync(user, NhTwoFactorSecurityEvent.ResetByAdministrator, cancellationToken: cancellationToken);
            },
            "Two-factor authentication reset by an administrator.",
            committedByUserId,
            cancellationToken);
    }

    public virtual async Task<TaskResult> VerifySecondFactorAsync(
        TUser user,
        string method,
        string code,
        CancellationToken cancellationToken = default)
    {
        var enrollment = await GetEnrollmentAsync(user, cancellationToken);
        if (!enrollment.Methods.Contains(method) || method == NhTwoFactorMethods.Passkey)
        {
            return NhTwoFactorFailureCodes.Fail(NhTwoFactorFailureCodes.MethodNotAllowed);
        }

        var check = await CheckCodeAsync(user, method, code);
        if (!check.IsCandidate)
        {
            return await RecordFailedAttemptAsync(user, cancellationToken);
        }

        var repository = _nhUserManager.GetRepository();
        await using var transaction = await repository.StartOrGetTransactionScopeAsync(cancellationToken);

        var commitResult = await CommitSecondFactorAsync(user, check, challenge: null, cancellationToken);
        if (!commitResult.Success)
        {
            await transaction.RollbackAsync(cancellationToken);
            return await RecordFailedAttemptAfterRollbackAsync(user, transaction.IsMyTransaction, cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return TaskResult.Succeeded();
    }

    internal Task<NhTwoFactorEvaluation> EvaluateAsync(
        TUser user,
        string factor,
        CancellationToken cancellationToken)
    {
        return EvaluateCoreAsync(user, factor, cancellationToken);
    }

    /// <summary>
    /// Whether the evaluation leaves the user a method that proves possession on its own.
    /// </summary>
    internal static IReadOnlyList<string> UsableMethods(NhTwoFactorEvaluation evaluation)
    {
        var methods = evaluation.Requirement.AllowedMethods
            .Intersect(evaluation.Enrollment.Methods, StringComparer.Ordinal)
            .ToList();

        if (!methods.Any(NhTwoFactorMethods.IsPrimary))
        {
            return [];
        }

        return methods;
    }

    internal async Task<NhTwoFactorChallengeResponse> CreateChallengeAsync(
        TUser user,
        string factor,
        IReadOnlyList<string> methods)
    {
        return await CreateStepAsync(
            user,
            factor,
            methods,
            NhTwoFactorTicketPurposes.Challenge,
            NhAuthenticationStepStatuses.TwoFactorRequired,
            _configuration.ChallengeLifetime);
    }

    internal async Task<NhTwoFactorChallengeResponse?> CreateEnrollmentAsync(TUser user, string factor)
    {
        var methods = _configuration.EnrollableMethods(requiredByPolicy: true);
        if (methods.Count == 0)
        {
            return null;
        }

        return await CreateStepAsync(
            user,
            factor,
            methods,
            NhTwoFactorTicketPurposes.Enrollment,
            NhAuthenticationStepStatuses.EnrollmentRequired,
            _configuration.EnrollmentLifetime);
    }

    internal NhTwoFactorTicket? ReadChallenge(string? challengeToken)
    {
        return _ticketProtector.Unprotect(challengeToken, NhTwoFactorTicketPurposes.Challenge);
    }

    internal NhTwoFactorTicket? ReadEnrollment(string? enrollmentToken)
    {
        return _ticketProtector.Unprotect(enrollmentToken, NhTwoFactorTicketPurposes.Enrollment);
    }

    /// <summary>
    /// Returns whether the ticket still belongs to the current account state and, for a
    /// challenge, was not completed before. Consumption itself happens atomically in
    /// <see cref="CommitSecondFactorAsync"/>.
    /// </summary>
    internal async Task<bool> IsTicketUsableAsync(TUser user, NhTwoFactorTicket ticket)
    {
        var securityStamp = await _userManager.GetSecurityStampAsync(user);
        if (!string.Equals(securityStamp, ticket.SecurityStamp, StringComparison.Ordinal))
        {
            return false;
        }

        if (ticket.Purpose != NhTwoFactorTicketPurposes.Challenge)
        {
            return true;
        }

        var stored = await _userManager.GetAuthenticationTokenAsync(user, LoginProvider, ConsumedChallengesTokenName);
        return ParseConsumedChallenges(stored).All(entry => entry.Nonce != ticket.Nonce);
    }

    internal Task<TaskResult<NhAuthenticatorSetupViewModel>> BeginEnrollmentAuthenticatorSetupAsync(TUser user)
    {
        if (!_configuration.AuthenticatorEnabled)
        {
            return Task.FromResult(
                NhTwoFactorFailureCodes.Fail<NhAuthenticatorSetupViewModel>(NhTwoFactorFailureCodes.MethodNotAllowed));
        }

        return BeginAuthenticatorSetupCoreAsync(user);
    }

    /// <summary>
    /// E-mails a sign-in code for a pending challenge. The code is only valid together with
    /// the challenge.
    /// </summary>
    internal async Task<TaskResult<NhTwoFactorEmailCodeSentResponse>> SendSignInEmailCodeAsync(
        TUser user,
        IReadOnlyList<string> allowedMethods,
        CancellationToken cancellationToken)
    {
        if (!allowedMethods.Contains(NhTwoFactorMethods.Email))
        {
            return NhTwoFactorFailureCodes.Fail<NhTwoFactorEmailCodeSentResponse>(NhTwoFactorFailureCodes.MethodNotAllowed);
        }

        return await SendEmailCodeAsync(user, NhTwoFactorEmailCodePurpose.SignIn, cancellationToken);
    }

    internal async Task<TaskResult<NhTwoFactorEmailCodeSentResponse>> SendEnrollmentEmailCodeAsync(
        TUser user,
        CancellationToken cancellationToken)
    {
        if (!_configuration.EnrollableMethods(requiredByPolicy: true).Contains(NhTwoFactorMethods.Email))
        {
            return NhTwoFactorFailureCodes.Fail<NhTwoFactorEmailCodeSentResponse>(NhTwoFactorFailureCodes.MethodNotAllowed);
        }

        return await SendEmailCodeAsync(user, NhTwoFactorEmailCodePurpose.Confirmation, cancellationToken);
    }

    /// <summary>
    /// Creates a remember-device token bound to the current security stamp.
    /// </summary>
    internal async Task<string> CreateRememberDeviceTokenAsync(TUser user)
    {
        var securityStamp = await _userManager.GetSecurityStampAsync(user);
        var ticket = new NhTwoFactorTicket(
            user.Id,
            securityStamp,
            NhTwoFactorTicketPurposes.RememberDevice,
            NhTwoFactorTicketPurposes.RememberDevice,
            Convert.ToHexString(RandomNumberGenerator.GetBytes(16)),
            _timeProvider.GetUtcNow().Add(_configuration.RememberDeviceLifetime));

        return _ticketProtector.Protect(ticket);
    }

    internal async Task<bool> IsRememberedDeviceAsync(TUser user, string? rememberDeviceToken)
    {
        if (!_configuration.RememberDeviceEnabled || string.IsNullOrWhiteSpace(rememberDeviceToken))
        {
            return false;
        }

        var ticket = _ticketProtector.Unprotect(rememberDeviceToken, NhTwoFactorTicketPurposes.RememberDevice);
        if (ticket == null || ticket.UserId != user.Id)
        {
            return false;
        }

        var securityStamp = await _userManager.GetSecurityStampAsync(user);
        return string.Equals(securityStamp, ticket.SecurityStamp, StringComparison.Ordinal);
    }

    /// <summary>
    /// Checks a code without writing. Authenticator and e-mail codes are fully verified;
    /// recovery codes are verified when they are redeemed in <see cref="CommitSecondFactorAsync"/>.
    /// </summary>
    internal async Task<NhSecondFactorCheck> CheckCodeAsync(TUser user, string method, string? code)
    {
        if (method == NhTwoFactorMethods.Authenticator && _configuration.AuthenticatorEnabled)
        {
            var key = await _userManager.GetAuthenticatorKeyAsync(user);
            if (string.IsNullOrEmpty(key))
            {
                return NhSecondFactorCheck.Invalid(method);
            }

            var lastStep = await GetLastAuthenticatorStepAsync(user);
            var matchedStep = NhTotp.Match(key, code, _timeProvider.GetUtcNow(), lastStep);
            if (!matchedStep.HasValue)
            {
                return NhSecondFactorCheck.Invalid(method);
            }

            return NhSecondFactorCheck.Authenticator(matchedStep.Value);
        }

        if (method == NhTwoFactorMethods.Email && _configuration.EmailCodesEnabled)
        {
            var storedCode = ReadEmailCode(await _userManager.GetAuthenticationTokenAsync(user, LoginProvider, EmailCodeTokenName));
            if (storedCode == null
                || storedCode.Purpose != NhTwoFactorEmailCodePurpose.SignIn
                || !MatchesEmailCode(user, storedCode, code))
            {
                return NhSecondFactorCheck.Invalid(method);
            }

            return NhSecondFactorCheck.Email();
        }

        if (method == NhTwoFactorMethods.RecoveryCode && _configuration.RecoveryCodesEnabled)
        {
            if (string.IsNullOrWhiteSpace(code))
            {
                return NhSecondFactorCheck.Invalid(method);
            }

            return NhSecondFactorCheck.RecoveryCode(code);
        }

        return NhSecondFactorCheck.Invalid(method);
    }

    /// <summary>
    /// Records a verified second factor inside the caller's transaction: consumes the
    /// challenge, stores the authenticator step, clears the e-mailed code, redeems the
    /// recovery code or stores the passkey's signature counter. Every write updates the user's concurrency stamp, so of two concurrent
    /// attempts only one commits.
    /// </summary>
    internal async Task<TaskResult> CommitSecondFactorAsync(
        TUser user,
        NhSecondFactorCheck check,
        NhTwoFactorTicket? challenge,
        CancellationToken cancellationToken)
    {
        if (challenge != null)
        {
            var consumed = await TryConsumeChallengeAsync(user, challenge);
            if (!consumed)
            {
                return NhTwoFactorFailureCodes.Fail(NhTwoFactorFailureCodes.ChallengeExpired);
            }
        }

        if (check.Method == NhTwoFactorMethods.Authenticator)
        {
            var stepResult = await SetLastAuthenticatorStepAsync(user, check.AuthenticatorStep!.Value);
            if (!stepResult.Succeeded)
            {
                return NhTwoFactorFailureCodes.Fail(NhTwoFactorFailureCodes.InvalidCode);
            }

            return TaskResult.Succeeded();
        }

        if (check.Method == NhTwoFactorMethods.Email)
        {
            try
            {
                var removeResult = await _userManager.RemoveAuthenticationTokenAsync(user, LoginProvider, EmailCodeTokenName);
                if (!removeResult.Succeeded)
                {
                    return NhTwoFactorFailureCodes.Fail(NhTwoFactorFailureCodes.InvalidCode);
                }
            }
            catch (DbUpdateException)
            {
                return NhTwoFactorFailureCodes.Fail(NhTwoFactorFailureCodes.ChallengeExpired);
            }

            return TaskResult.Succeeded();
        }

        if (check.Method == NhTwoFactorMethods.RecoveryCode)
        {
            IdentityResult redeemResult;
            try
            {
                redeemResult = await _userManager.RedeemTwoFactorRecoveryCodeAsync(user, check.RecoveryCodeValue!);
            }
            catch (DbUpdateException)
            {
                return NhTwoFactorFailureCodes.Fail(NhTwoFactorFailureCodes.ChallengeExpired);
            }

            if (!redeemResult.Succeeded)
            {
                return NhTwoFactorFailureCodes.Fail(NhTwoFactorFailureCodes.InvalidCode);
            }

            var recoveryCodesLeft = await _userManager.CountRecoveryCodesAsync(user);
            return await NotifyAsync(user, NhTwoFactorSecurityEvent.RecoveryCodeUsed, recoveryCodesLeft, cancellationToken);
        }

        if (check.Method == NhTwoFactorMethods.Passkey)
        {
            return await StorePasskeyUseAsync(user, check.PasskeyValue!);
        }

        return NhTwoFactorFailureCodes.Fail(NhTwoFactorFailureCodes.MethodNotAllowed);
    }

    /// <summary>
    /// Counts a failed second-factor attempt against the Identity lockout.
    /// </summary>
    internal async Task<TaskResult> RecordFailedAttemptAsync(TUser user, CancellationToken cancellationToken)
    {
        await _userManager.AccessFailedAsync(user);

        if (!await _userManager.IsLockedOutAsync(user))
        {
            return NhTwoFactorFailureCodes.Fail(NhTwoFactorFailureCodes.InvalidCode);
        }

        var notificationResult = await NotifyAsync(user, NhTwoFactorSecurityEvent.LockedOut, cancellationToken: cancellationToken);
        if (!notificationResult.Success)
        {
            // The lockout itself is already stored; the user still receives the lockout result.
            _logger.LogWarning(
                "Could not create the lockout security notification for user {UserId}: {Errors}",
                user.Id,
                string.Join("; ", notificationResult.AllErrorMessages));
        }

        return NhTwoFactorFailureCodes.Fail(NhTwoFactorFailureCodes.LockedOut);
    }

    /// <summary>
    /// Records a failed attempt after a rolled-back transaction. The tracked user still has
    /// the concurrency stamp of the rolled-back write, so it is reloaded first.
    /// </summary>
    internal async Task<TaskResult> RecordFailedAttemptAfterRollbackAsync(
        TUser user,
        bool ownsTransaction,
        CancellationToken cancellationToken)
    {
        if (!ownsTransaction)
        {
            return NhTwoFactorFailureCodes.Fail(NhTwoFactorFailureCodes.InvalidCode);
        }

        _nhUserManager.GetRepository().ClearTracking();

        var reloadedUser = await _userManager.FindByIdAsync(user.Id.ToString());
        if (reloadedUser == null)
        {
            return NhTwoFactorFailureCodes.Fail(NhTwoFactorFailureCodes.InvalidCode);
        }

        return await RecordFailedAttemptAsync(reloadedUser, cancellationToken);
    }

    /// <summary>
    /// Sends an enrollment reminder to a user whom the policy requires to enroll.
    /// </summary>
    internal async Task<TaskResult> SendEnrollmentReminderAsync(TUser user, CancellationToken cancellationToken)
    {
        return await NotifyAsync(user, NhTwoFactorSecurityEvent.EnrollmentReminder, cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Ends every session of a user so the next sign-in applies the current policy.
    /// </summary>
    internal async Task<TaskResult> EndSessionsAsync(TUser user, Guid? committedByUserId, CancellationToken cancellationToken)
    {
        return await ExecuteSecurityMutationAsync(
            user,
            async () =>
            {
                var stampResult = await _userManager.UpdateSecurityStampAsync(user);
                if (!stampResult.Succeeded)
                {
                    return IdentityFailure(stampResult);
                }

                return TaskResult.Succeeded();
            },
            "Sessions ended to apply the two-factor policy.",
            committedByUserId,
            cancellationToken);
    }

    private async Task<NhTwoFactorEvaluation> EvaluateCoreAsync(
        TUser user,
        string factor,
        CancellationToken cancellationToken)
    {
        var enrollment = await GetEnrollmentAsync(user, cancellationToken);
        var context = new NhTwoFactorPolicyContext(
            user.Id,
            factor,
            enrollment.IsEnrolled,
            enrollment.Methods,
            async token =>
            {
                var claims = await _nhUserManager.GetValidClaims(user, withDivision: false, token);
                return (IReadOnlyList<Claim>)claims;
            });

        var requirement = await _policy.EvaluateAsync(context, cancellationToken);
        return new NhTwoFactorEvaluation(requirement, enrollment);
    }

    private async Task<NhTwoFactorChallengeResponse> CreateStepAsync(
        TUser user,
        string factor,
        IReadOnlyList<string> methods,
        string purpose,
        string status,
        TimeSpan lifetime)
    {
        var securityStamp = await _userManager.GetSecurityStampAsync(user);
        if (string.IsNullOrEmpty(securityStamp))
        {
            throw new InvalidOperationException("The user does not have a security stamp.");
        }

        var expiresAt = _timeProvider.GetUtcNow().Add(lifetime);
        var ticket = new NhTwoFactorTicket(
            user.Id,
            securityStamp,
            purpose,
            factor,
            Convert.ToHexString(RandomNumberGenerator.GetBytes(16)),
            expiresAt);

        return new NhTwoFactorChallengeResponse
        {
            Status = status,
            ChallengeToken = _ticketProtector.Protect(ticket),
            ExpiresAt = expiresAt,
            Methods = methods,
        };
    }

    private async Task<TaskResult<NhAuthenticatorSetupViewModel>> BeginAuthenticatorSetupCoreAsync(TUser user)
    {
        if (!_configuration.AuthenticatorEnabled)
        {
            return NhTwoFactorFailureCodes.Fail<NhAuthenticatorSetupViewModel>(NhTwoFactorFailureCodes.MethodNotAllowed);
        }

        var key = _userManager.GenerateNewAuthenticatorKey();
        var storeResult = await _userManager.SetAuthenticationTokenAsync(
            user,
            LoginProvider,
            PendingAuthenticatorKeyTokenName,
            key);

        if (!storeResult.Succeeded)
        {
            return IdentityFailure<NhAuthenticatorSetupViewModel>(storeResult);
        }

        var accountName = await _userManager.GetEmailAsync(user)
            ?? await _userManager.GetUserNameAsync(user)
            ?? user.Id.ToString();
        var authenticatorUri = NhTotp.CreateAuthenticatorUri(GetIssuer(), accountName, key);

        return new NhAuthenticatorSetupViewModel
        {
            SharedKey = NhTotp.FormatSharedKey(key),
            AuthenticatorUri = authenticatorUri,
            QrCodeDataUri = _qrCodeRenderer.RenderPngDataUri(authenticatorUri),
        };
    }

    private async Task<TaskResult<NhTwoFactorEmailCodeSentResponse>> SendEmailCodeAsync(
        TUser user,
        NhTwoFactorEmailCodePurpose purpose,
        CancellationToken cancellationToken)
    {
        var email = await _userManager.GetEmailAsync(user);
        if (string.IsNullOrWhiteSpace(email) || !await _userManager.IsEmailConfirmedAsync(user))
        {
            return NhTwoFactorFailureCodes.Fail<NhTwoFactorEmailCodeSentResponse>(NhTwoFactorFailureCodes.EmailUnavailable);
        }

        if (_notificationService == null)
        {
            throw new InvalidOperationException("E-mail codes require WithNotifications(...).");
        }

        var now = _timeProvider.GetUtcNow();
        var previousCode = ReadEmailCode(await _userManager.GetAuthenticationTokenAsync(user, LoginProvider, EmailCodeTokenName));
        if (previousCode != null && previousCode.SentAt.Add(_configuration.EmailCodeResendCooldown) > now)
        {
            return NhTwoFactorFailureCodes.Fail<NhTwoFactorEmailCodeSentResponse>(NhTwoFactorFailureCodes.EmailCooldown);
        }

        var code = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6", CultureInfo.InvariantCulture);
        var storedCode = new NhStoredEmailCode(purpose, HashEmailCode(user, code), now.Add(_configuration.EmailCodeLifetime), now);

        var repository = _nhUserManager.GetRepository();
        await using var transaction = await repository.StartOrGetTransactionScopeAsync(cancellationToken);

        // Storing the new code replaces the previous one, so only the latest e-mailed code works.
        var storeResult = await _userManager.SetAuthenticationTokenAsync(user, LoginProvider, EmailCodeTokenName, storedCode.Serialize());
        if (!storeResult.Succeeded)
        {
            await transaction.RollbackAsync(cancellationToken);
            return IdentityFailure<NhTwoFactorEmailCodeSentResponse>(storeResult);
        }

        var notification = await _messageComposer.ComposeEmailCodeAsync(
            new NhTwoFactorEmailCodeMessage
            {
                UserId = user.Id,
                Email = email,
                Code = code,
                ExpiresAt = storedCode.ExpiresAt,
                Purpose = purpose,
            },
            cancellationToken);

        var notificationResult = await _notificationService.CreateAsync(notification, cancellationToken);
        if (!notificationResult.Success)
        {
            await transaction.RollbackAsync(cancellationToken);
            return TaskResult<NhTwoFactorEmailCodeSentResponse>.Failed(notificationResult);
        }

        await transaction.CommitAsync(cancellationToken);

        return new NhTwoFactorEmailCodeSentResponse
        {
            ExpiresAt = storedCode.ExpiresAt,
            ResendAvailableAt = now.Add(_configuration.EmailCodeResendCooldown),
        };
    }

    /// <summary>
    /// Creates a security notification for the user when security notifications are enabled.
    /// Call it inside the mutation's transaction, so the notification commits with the change.
    /// </summary>
    private async Task<TaskResult> NotifyAsync(
        TUser user,
        NhTwoFactorSecurityEvent securityEvent,
        int? recoveryCodesLeft = null,
        CancellationToken cancellationToken = default)
    {
        var isReminder = securityEvent == NhTwoFactorSecurityEvent.EnrollmentReminder;
        if ((!_configuration.SecurityNotificationsEnabled && !isReminder) || _notificationService == null)
        {
            return TaskResult.Succeeded();
        }

        var notification = await _messageComposer.ComposeSecurityEventAsync(
            new NhTwoFactorSecurityEventMessage
            {
                UserId = user.Id,
                Email = await _userManager.GetEmailAsync(user),
                Event = securityEvent,
                OccurredAt = _timeProvider.GetUtcNow(),
                RecoveryCodesLeft = recoveryCodesLeft,
                IncludeEmail = isReminder || _configuration.SecurityNotificationsByEmail,
                IncludeInApp = isReminder || _configuration.SecurityNotificationsInApp,
            },
            cancellationToken);

        if (notification == null)
        {
            return TaskResult.Succeeded();
        }

        var result = await _notificationService.CreateAsync(notification, cancellationToken);
        if (!result.Success)
        {
            return TaskResult.Failed(result);
        }

        return TaskResult.Succeeded();
    }

    private async Task<TaskResult<IReadOnlyList<string>?>> EnsureRecoveryCodesAsync(TUser user, bool replaceExisting)
    {
        if (!_configuration.RecoveryCodesEnabled)
        {
            return new TaskResult<IReadOnlyList<string>?>();
        }

        if (!replaceExisting && await _userManager.CountRecoveryCodesAsync(user) > 0)
        {
            return new TaskResult<IReadOnlyList<string>?>();
        }

        var generatedCodes = await _userManager.GenerateNewTwoFactorRecoveryCodesAsync(user, _configuration.RecoveryCodeCount);
        if (generatedCodes == null)
        {
            throw new InvalidOperationException("Could not store the two-factor recovery codes.");
        }

        return new TaskResult<IReadOnlyList<string>?> { Data = generatedCodes.ToList() };
    }

    private async Task<TaskResult> RemoveSecondFactorsAsync(TUser user, CancellationToken cancellationToken)
    {
        if (_userStore is not IUserTwoFactorRecoveryCodeStore<TUser> recoveryCodeStore)
        {
            throw new NotSupportedException("The configured user store does not support two-factor recovery codes.");
        }

        await recoveryCodeStore.ReplaceCodesAsync(user, [], cancellationToken);

        var disableResult = await _userManager.SetTwoFactorEnabledAsync(user, false);
        if (!disableResult.Succeeded)
        {
            return IdentityFailure(disableResult);
        }

        // Clearing the key makes the old authenticator codes worthless, and the authenticator
        // stays unenrolled when another method enables two-factor authentication again.
        if (_userStore is not IUserAuthenticatorKeyStore<TUser> authenticatorKeyStore)
        {
            throw new NotSupportedException("The configured user store does not support authenticator keys.");
        }

        await authenticatorKeyStore.SetAuthenticatorKeyAsync(user, null!, cancellationToken);

        var removePasskeysResult = await RemoveAllPasskeysAsync(user);
        if (!removePasskeysResult.Success)
        {
            return removePasskeysResult;
        }

        foreach (var tokenName in new[]
                 {
                     PendingAuthenticatorKeyTokenName,
                     LastAuthenticatorStepTokenName,
                     EmailFactorTokenName,
                     EmailCodeTokenName,
                 })
        {
            var removeResult = await _userManager.RemoveAuthenticationTokenAsync(user, LoginProvider, tokenName);
            if (!removeResult.Succeeded)
            {
                return IdentityFailure(removeResult);
            }
        }

        return TaskResult.Succeeded();
    }

    private async Task<TaskResult> VerifyReauthenticationAsync(
        TUser user,
        NhTwoFactorReauthentication? reauthentication,
        CancellationToken cancellationToken)
    {
        if (reauthentication == null)
        {
            return NhTwoFactorFailureCodes.Fail(NhTwoFactorFailureCodes.ReauthenticationRequired);
        }

        if (!string.IsNullOrEmpty(reauthentication.Password))
        {
            if (await _userManager.IsLockedOutAsync(user))
            {
                return NhTwoFactorFailureCodes.Fail(NhTwoFactorFailureCodes.LockedOut);
            }

            if (await _userManager.CheckPasswordAsync(user, reauthentication.Password))
            {
                return TaskResult.Succeeded();
            }

            var failure = await RecordFailedAttemptAsync(user, cancellationToken);
            return MapReauthenticationFailure(failure);
        }

        if (!string.IsNullOrEmpty(reauthentication.Method) && !string.IsNullOrEmpty(reauthentication.Code))
        {
            var verification = await VerifySecondFactorAsync(
                user,
                reauthentication.Method,
                reauthentication.Code,
                cancellationToken);

            return MapReauthenticationFailure(verification);
        }

        return NhTwoFactorFailureCodes.Fail(NhTwoFactorFailureCodes.ReauthenticationRequired);
    }

    private static TaskResult MapReauthenticationFailure(TaskResult result)
    {
        if (result.Success || NhTwoFactorFailureCodes.Has(result, NhTwoFactorFailureCodes.LockedOut))
        {
            return result;
        }

        return NhTwoFactorFailureCodes.Fail(NhTwoFactorFailureCodes.ReauthenticationFailed);
    }

    private async Task<TaskResult> ExecuteSecurityMutationAsync(
        TUser user,
        Func<Task<TaskResult>> mutation,
        string logMessage,
        Guid? committedByUserId,
        CancellationToken cancellationToken)
    {
        return await NhSecurityMutationOperations.ExecuteWithSessionInvalidationAsync(
            _userManager,
            _nhUserManager.GetRepository(),
            _dbLogService,
            user,
            mutation,
            logMessage,
            committedByUserId,
            GetType().Name,
            cancellationToken);
    }

    private async Task<bool> HasConfirmedEmailFactorAsync(TUser user)
    {
        var confirmedEmail = await _userManager.GetAuthenticationTokenAsync(user, LoginProvider, EmailFactorTokenName);
        if (string.IsNullOrEmpty(confirmedEmail))
        {
            return false;
        }

        // A changed e-mail address must be confirmed again before it receives sign-in codes.
        var currentEmail = NormalizeEmail(await _userManager.GetEmailAsync(user));
        return string.Equals(confirmedEmail, currentEmail, StringComparison.Ordinal)
            && await _userManager.IsEmailConfirmedAsync(user);
    }

    private static string NormalizeEmail(string? email)
    {
        return (email ?? string.Empty).Trim().ToUpperInvariant();
    }

    private static string HashEmailCode(TUser user, string code)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(user.Id.ToString("N") + ":" + code));
        return Convert.ToBase64String(hash);
    }

    private bool MatchesEmailCode(TUser user, NhStoredEmailCode storedCode, string? code)
    {
        if (string.IsNullOrWhiteSpace(code) || storedCode.ExpiresAt <= _timeProvider.GetUtcNow())
        {
            return false;
        }

        var presented = Encoding.ASCII.GetBytes(HashEmailCode(user, code.Trim()));
        var expected = Encoding.ASCII.GetBytes(storedCode.Hash);
        return CryptographicOperations.FixedTimeEquals(presented, expected);
    }

    private static NhStoredEmailCode? ReadEmailCode(string? stored)
    {
        return NhStoredEmailCode.Parse(stored);
    }

    private async Task<bool> TryConsumeChallengeAsync(TUser user, NhTwoFactorTicket challenge)
    {
        var now = _timeProvider.GetUtcNow();
        var stored = await _userManager.GetAuthenticationTokenAsync(user, LoginProvider, ConsumedChallengesTokenName);
        var entries = ParseConsumedChallenges(stored)
            .Where(entry => entry.ExpiresAt > now)
            .ToList();

        if (entries.Any(entry => entry.Nonce == challenge.Nonce))
        {
            return false;
        }

        entries.Add((challenge.Nonce, challenge.ExpiresAt));

        var value = string.Join(
            ';',
            entries.Select(entry => entry.Nonce + "|" + entry.ExpiresAt.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)));

        try
        {
            var result = await _userManager.SetAuthenticationTokenAsync(user, LoginProvider, ConsumedChallengesTokenName, value);
            return result.Succeeded;
        }
        catch (DbUpdateException)
        {
            // A concurrent attempt inserted the same token row first.
            return false;
        }
    }

    private static IEnumerable<(string Nonce, DateTimeOffset ExpiresAt)> ParseConsumedChallenges(string? stored)
    {
        if (string.IsNullOrEmpty(stored))
        {
            yield break;
        }

        foreach (var entry in stored.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = entry.Split('|');
            if (parts.Length != 2
                || !long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var expiresAtSeconds))
            {
                continue;
            }

            yield return (parts[0], DateTimeOffset.FromUnixTimeSeconds(expiresAtSeconds));
        }
    }

    private async Task<long?> GetLastAuthenticatorStepAsync(TUser user)
    {
        var stored = await _userManager.GetAuthenticationTokenAsync(user, LoginProvider, LastAuthenticatorStepTokenName);
        if (long.TryParse(stored, NumberStyles.Integer, CultureInfo.InvariantCulture, out var step))
        {
            return step;
        }

        return null;
    }

    private async Task<IdentityResult> SetLastAuthenticatorStepAsync(TUser user, long step)
    {
        var lastStep = await GetLastAuthenticatorStepAsync(user);
        if (lastStep.HasValue && step <= lastStep.Value)
        {
            return IdentityResult.Failed(_userManager.ErrorDescriber.ConcurrencyFailure());
        }

        try
        {
            return await _userManager.SetAuthenticationTokenAsync(
                user,
                LoginProvider,
                LastAuthenticatorStepTokenName,
                step.ToString(CultureInfo.InvariantCulture));
        }
        catch (DbUpdateException)
        {
            // A concurrent attempt inserted the same token row first.
            return IdentityResult.Failed(_userManager.ErrorDescriber.ConcurrencyFailure());
        }
    }

    private Task LogAsync(TUser user, string message, Guid? committedByUserId, CancellationToken cancellationToken)
    {
        return _dbLogService.LogAsync(
            message: message,
            messageArguments: new[] { user.Id.ToString() },
            objectId: user.Id.ToString(),
            objectType: typeof(TUser).Name,
            objectTypeFull: typeof(TUser).FullName,
            userId: committedByUserId,
            action: LogAction.Update,
            type: LogType.Information,
            source: LogSource.Internal,
            tag: GetType().Name,
            cancellationToken: cancellationToken);
    }

    private string GetIssuer()
    {
        if (!string.IsNullOrWhiteSpace(_configuration.AuthenticatorIssuer))
        {
            return _configuration.AuthenticatorIssuer;
        }

        return _hostEnvironment.ApplicationName;
    }

    private static TaskResult IdentityFailure(IdentityResult identityResult)
    {
        var result = new TaskResult();
        foreach (var error in identityResult.Errors)
        {
            result.AddError(error.Code, error.Description);
        }

        return result;
    }

    private static TaskResult<T> IdentityFailure<T>(IdentityResult identityResult)
    {
        return TaskResult<T>.Failed(IdentityFailure(identityResult));
    }
}

internal sealed record NhTwoFactorEvaluation(NhTwoFactorRequirement Requirement, NhTwoFactorEnrollment Enrollment);

/// <summary>
/// An e-mailed code as stored in the Identity token table: only a hash, the purpose and the
/// timestamps are kept.
/// </summary>
internal sealed record NhStoredEmailCode(
    NhTwoFactorEmailCodePurpose Purpose,
    string Hash,
    DateTimeOffset ExpiresAt,
    DateTimeOffset SentAt)
{
    internal string Serialize()
    {
        return string.Join(
            '|',
            ((int)Purpose).ToString(CultureInfo.InvariantCulture),
            Hash,
            ExpiresAt.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
            SentAt.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));
    }

    internal static NhStoredEmailCode? Parse(string? stored)
    {
        if (string.IsNullOrEmpty(stored))
        {
            return null;
        }

        var parts = stored.Split('|');
        if (parts.Length != 4
            || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var purpose)
            || !long.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var expiresAt)
            || !long.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var sentAt))
        {
            return null;
        }

        return new NhStoredEmailCode(
            (NhTwoFactorEmailCodePurpose)purpose,
            parts[1],
            DateTimeOffset.FromUnixTimeSeconds(expiresAt),
            DateTimeOffset.FromUnixTimeSeconds(sentAt));
    }
}

internal sealed class NhSecondFactorCheck
{
    private NhSecondFactorCheck(
        string method,
        bool isCandidate,
        long? authenticatorStep,
        string? recoveryCodeValue,
        UserPasskeyInfo? passkeyValue = null)
    {
        Method = method;
        IsCandidate = isCandidate;
        AuthenticatorStep = authenticatorStep;
        RecoveryCodeValue = recoveryCodeValue;
        PasskeyValue = passkeyValue;
    }

    internal string Method { get; }

    /// <summary>Whether the code can be committed. Recovery codes are verified on redemption.</summary>
    internal bool IsCandidate { get; }

    internal long? AuthenticatorStep { get; }

    internal string? RecoveryCodeValue { get; }

    /// <summary>The verified passkey with its updated signature counter.</summary>
    internal UserPasskeyInfo? PasskeyValue { get; }

    internal static NhSecondFactorCheck Invalid(string method)
    {
        return new NhSecondFactorCheck(method, false, null, null);
    }

    internal static NhSecondFactorCheck Authenticator(long step)
    {
        return new NhSecondFactorCheck(NhTwoFactorMethods.Authenticator, true, step, null);
    }

    internal static NhSecondFactorCheck Email()
    {
        return new NhSecondFactorCheck(NhTwoFactorMethods.Email, true, null, null);
    }

    internal static NhSecondFactorCheck RecoveryCode(string code)
    {
        return new NhSecondFactorCheck(NhTwoFactorMethods.RecoveryCode, true, null, code);
    }

    internal static NhSecondFactorCheck Passkey(UserPasskeyInfo passkey)
    {
        return new NhSecondFactorCheck(NhTwoFactorMethods.Passkey, true, null, null, passkey);
    }
}
