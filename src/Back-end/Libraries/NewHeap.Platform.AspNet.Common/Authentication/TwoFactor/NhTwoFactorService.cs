using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using NewHeap.Platform.AspNet.Common.DAL.Entities;
using NewHeap.Platform.AspNet.Common.Services;
using NewHeap.Platform.Common.Models;
using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;

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
    /// Verifies a second-factor code outside a sign-in, for example as reauthentication.
    /// A wrong code counts as a failed access attempt.
    /// </summary>
    Task<TaskResult> VerifySecondFactorAsync(
        TUser user,
        string method,
        string code,
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
        bool authenticatorSetupPending)
    {
        IsEnrolled = isEnrolled;
        Methods = methods;
        RecoveryCodesLeft = recoveryCodesLeft;
        AuthenticatorSetupPending = authenticatorSetupPending;
    }

    public bool IsEnrolled { get; }
    public IReadOnlyList<string> Methods { get; }
    public int RecoveryCodesLeft { get; }
    public bool AuthenticatorSetupPending { get; }
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
/// codes and replay state live in the Identity user-token table, so no schema change is
/// needed.
/// </summary>
public class NhTwoFactorService<TUser> : INhTwoFactorService<TUser>
    where TUser : IdentityUser<Guid>
{
    private const string LoginProvider = NhAuthenticationSessionDefaults.LoginProvider;
    private const string PendingAuthenticatorKeyTokenName = "TwoFactorPendingAuthenticatorKey";
    private const string LastAuthenticatorStepTokenName = "TwoFactorLastAuthenticatorStep";
    private const string ConsumedChallengesTokenName = "TwoFactorConsumedChallenges";

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
        TimeProvider timeProvider)
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
    }

    public virtual async Task<NhTwoFactorEnrollment> GetEnrollmentAsync(
        TUser user,
        CancellationToken cancellationToken = default)
    {
        var enabled = await _userManager.GetTwoFactorEnabledAsync(user);
        var hasAuthenticator = _configuration.AuthenticatorEnabled
            && !string.IsNullOrEmpty(await _userManager.GetAuthenticatorKeyAsync(user));

        var recoveryCodesLeft = 0;
        if (_configuration.RecoveryCodesEnabled)
        {
            recoveryCodesLeft = await _userManager.CountRecoveryCodesAsync(user);
        }

        var pendingKey = await _userManager.GetAuthenticationTokenAsync(
            user,
            LoginProvider,
            PendingAuthenticatorKeyTokenName);

        var isEnrolled = enabled && hasAuthenticator;
        var methods = new List<string>();
        if (isEnrolled)
        {
            methods.Add(NhTwoFactorMethods.Authenticator);

            if (recoveryCodesLeft > 0)
            {
                methods.Add(NhTwoFactorMethods.RecoveryCode);
            }
        }

        return new NhTwoFactorEnrollment(isEnrolled, methods, recoveryCodesLeft, !string.IsNullOrEmpty(pendingKey));
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
        var enrollment = await GetEnrollmentAsync(user, cancellationToken);
        var requiredWithoutEnrollment = await EvaluateAsync(
            user,
            NhAuthenticationFactors.Password,
            enrollmentOverride: new NhTwoFactorEnrollment(false, [], 0, false),
            cancellationToken);

        return new NhTwoFactorStatusViewModel
        {
            Enabled = enrollment.IsEnrolled,
            Required = requiredWithoutEnrollment.Requirement.Required,
            Methods = enrollment.Methods,
            AvailableMethods = _configuration.AvailableMethods,
            RecoveryCodesLeft = enrollment.RecoveryCodesLeft,
            AuthenticatorSetupPending = enrollment.AuthenticatorSetupPending,
        };
    }

    public virtual async Task<TaskResult<NhAuthenticatorSetupViewModel>> BeginAuthenticatorSetupAsync(
        TUser user,
        NhTwoFactorReauthentication? reauthentication,
        CancellationToken cancellationToken = default)
    {
        if (!_configuration.AuthenticatorEnabled)
        {
            return NhTwoFactorFailureCodes.Fail<NhAuthenticatorSetupViewModel>(NhTwoFactorFailureCodes.MethodNotAllowed);
        }

        var enrollment = await GetEnrollmentAsync(user, cancellationToken);
        if (enrollment.IsEnrolled)
        {
            var reauthenticationResult = await VerifyReauthenticationAsync(user, reauthentication, cancellationToken);
            if (!reauthenticationResult.Success)
            {
                return TaskResult<NhAuthenticatorSetupViewModel>.Failed(reauthenticationResult);
            }
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

                if (_configuration.RecoveryCodesEnabled)
                {
                    var generatedCodes = await _userManager.GenerateNewTwoFactorRecoveryCodesAsync(
                        user,
                        _configuration.RecoveryCodeCount);
                    if (generatedCodes == null)
                    {
                        throw new InvalidOperationException("Could not store the two-factor recovery codes.");
                    }

                    recoveryCodes = generatedCodes.ToList();
                }

                return TaskResult.Succeeded();
            },
            "Two-factor authentication enabled.",
            committedByUserId,
            cancellationToken);

        if (!mutationResult.Success)
        {
            return TaskResult<NhTwoFactorChangeResult>.Failed(mutationResult);
        }

        return new NhTwoFactorChangeResult(recoveryCodes, new NhAuthenticationProof(user.Id, [NhTwoFactorMethods.Authenticator]));
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

        var recoveryCodes = await _userManager.GenerateNewTwoFactorRecoveryCodesAsync(
            user,
            _configuration.RecoveryCodeCount);
        if (recoveryCodes == null)
        {
            throw new InvalidOperationException("Could not store the two-factor recovery codes.");
        }

        await LogAsync(user, "Two-factor recovery codes regenerated.", committedByUserId, cancellationToken);

        return new NhTwoFactorChangeResult(recoveryCodes.ToList(), renewalProof: null);
    }

    public virtual async Task<TaskResult<NhTwoFactorChangeResult>> DisableAsync(
        TUser user,
        NhTwoFactorReauthentication? reauthentication,
        Guid? committedByUserId,
        CancellationToken cancellationToken = default)
    {
        var enrollment = await GetEnrollmentAsync(user, cancellationToken);
        if (!enrollment.IsEnrolled)
        {
            return NhTwoFactorFailureCodes.Fail<NhTwoFactorChangeResult>(NhTwoFactorFailureCodes.NotEnabled);
        }

        var status = await GetStatusAsync(user, cancellationToken);
        if (status.Required)
        {
            return NhTwoFactorFailureCodes.Fail<NhTwoFactorChangeResult>(NhTwoFactorFailureCodes.RequiredByPolicy);
        }

        var reauthenticationResult = await VerifyReauthenticationAsync(user, reauthentication, cancellationToken);
        if (!reauthenticationResult.Success)
        {
            return TaskResult<NhTwoFactorChangeResult>.Failed(reauthenticationResult);
        }

        if (_userStore is not IUserTwoFactorRecoveryCodeStore<TUser> recoveryCodeStore)
        {
            throw new NotSupportedException("The configured user store does not support two-factor recovery codes.");
        }

        var mutationResult = await ExecuteSecurityMutationAsync(
            user,
            async () =>
            {
                await recoveryCodeStore.ReplaceCodesAsync(user, [], cancellationToken);

                var disableResult = await _userManager.SetTwoFactorEnabledAsync(user, false);
                if (!disableResult.Succeeded)
                {
                    return IdentityFailure(disableResult);
                }

                // Replacing the key makes the old authenticator codes worthless even if two-factor
                // authentication is enabled again later.
                var resetKeyResult = await _userManager.ResetAuthenticatorKeyAsync(user);
                if (!resetKeyResult.Succeeded)
                {
                    return IdentityFailure(resetKeyResult);
                }

                await _userManager.RemoveAuthenticationTokenAsync(user, LoginProvider, PendingAuthenticatorKeyTokenName);
                await _userManager.RemoveAuthenticationTokenAsync(user, LoginProvider, LastAuthenticatorStepTokenName);

                return TaskResult.Succeeded();
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

    public virtual async Task<TaskResult> VerifySecondFactorAsync(
        TUser user,
        string method,
        string code,
        CancellationToken cancellationToken = default)
    {
        var enrollment = await GetEnrollmentAsync(user, cancellationToken);
        if (!enrollment.Methods.Contains(method))
        {
            return NhTwoFactorFailureCodes.Fail(NhTwoFactorFailureCodes.MethodNotAllowed);
        }

        var check = await CheckCodeAsync(user, method, code);
        if (!check.IsCandidate)
        {
            return await RecordFailedAttemptAsync(user);
        }

        var repository = _nhUserManager.GetRepository();
        await using var transaction = await repository.StartOrGetTransactionScopeAsync(cancellationToken);

        var commitResult = await CommitSecondFactorAsync(user, check, challenge: null);
        if (!commitResult.Success)
        {
            await transaction.RollbackAsync(cancellationToken);
            return await RecordFailedAttemptAfterRollbackAsync(user, transaction.IsMyTransaction);
        }

        await transaction.CommitAsync(cancellationToken);
        return TaskResult.Succeeded();
    }

    internal async Task<NhTwoFactorEvaluation> EvaluateAsync(
        TUser user,
        string factor,
        CancellationToken cancellationToken)
    {
        return await EvaluateAsync(user, factor, enrollmentOverride: null, cancellationToken);
    }

    internal async Task<NhTwoFactorChallengeResponse> CreateChallengeAsync(
        TUser user,
        string factor,
        IReadOnlyList<string> methods)
    {
        var securityStamp = await _userManager.GetSecurityStampAsync(user);
        if (string.IsNullOrEmpty(securityStamp))
        {
            throw new InvalidOperationException("The user does not have a security stamp.");
        }

        var expiresAt = _timeProvider.GetUtcNow().Add(_configuration.ChallengeLifetime);
        var ticket = new NhTwoFactorTicket(
            user.Id,
            securityStamp,
            NhTwoFactorTicketPurposes.Challenge,
            factor,
            Convert.ToHexString(RandomNumberGenerator.GetBytes(16)),
            expiresAt);

        return new NhTwoFactorChallengeResponse
        {
            Status = NhAuthenticationStepStatuses.TwoFactorRequired,
            ChallengeToken = _ticketProtector.Protect(ticket),
            ExpiresAt = expiresAt,
            Methods = methods,
        };
    }

    internal NhTwoFactorTicket? ReadChallenge(string? challengeToken)
    {
        return _ticketProtector.Unprotect(challengeToken, NhTwoFactorTicketPurposes.Challenge);
    }

    /// <summary>
    /// Returns whether the challenge still belongs to the current account state and was not
    /// completed before. Consumption itself happens atomically in <see cref="CommitSecondFactorAsync"/>.
    /// </summary>
    internal async Task<bool> IsChallengeUsableAsync(TUser user, NhTwoFactorTicket ticket)
    {
        var securityStamp = await _userManager.GetSecurityStampAsync(user);
        if (!string.Equals(securityStamp, ticket.SecurityStamp, StringComparison.Ordinal))
        {
            return false;
        }

        var stored = await _userManager.GetAuthenticationTokenAsync(user, LoginProvider, ConsumedChallengesTokenName);
        return ParseConsumedChallenges(stored).All(entry => entry.Nonce != ticket.Nonce);
    }

    /// <summary>
    /// Checks a code without writing. Authenticator codes are fully verified; recovery codes
    /// are verified when they are redeemed in <see cref="CommitSecondFactorAsync"/>.
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
    /// challenge, stores the authenticator step or redeems the recovery code. Every write
    /// updates the user's concurrency stamp, so of two concurrent attempts only one commits.
    /// </summary>
    internal async Task<TaskResult> CommitSecondFactorAsync(
        TUser user,
        NhSecondFactorCheck check,
        NhTwoFactorTicket? challenge)
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

            return TaskResult.Succeeded();
        }

        return NhTwoFactorFailureCodes.Fail(NhTwoFactorFailureCodes.MethodNotAllowed);
    }

    /// <summary>
    /// Counts a failed second-factor attempt against the Identity lockout.
    /// </summary>
    internal async Task<TaskResult> RecordFailedAttemptAsync(TUser user)
    {
        await _userManager.AccessFailedAsync(user);

        if (await _userManager.IsLockedOutAsync(user))
        {
            return NhTwoFactorFailureCodes.Fail(NhTwoFactorFailureCodes.LockedOut);
        }

        return NhTwoFactorFailureCodes.Fail(NhTwoFactorFailureCodes.InvalidCode);
    }

    /// <summary>
    /// Records a failed attempt after a rolled-back transaction. The tracked user still has
    /// the concurrency stamp of the rolled-back write, so it is reloaded first.
    /// </summary>
    internal async Task<TaskResult> RecordFailedAttemptAfterRollbackAsync(TUser user, bool ownsTransaction)
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

        return await RecordFailedAttemptAsync(reloadedUser);
    }

    private async Task<NhTwoFactorEvaluation> EvaluateAsync(
        TUser user,
        string factor,
        NhTwoFactorEnrollment? enrollmentOverride,
        CancellationToken cancellationToken)
    {
        var enrollment = enrollmentOverride ?? await GetEnrollmentAsync(user, cancellationToken);
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

            var failure = await RecordFailedAttemptAsync(user);
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

internal sealed class NhSecondFactorCheck
{
    private NhSecondFactorCheck(string method, bool isCandidate, long? authenticatorStep, string? recoveryCodeValue)
    {
        Method = method;
        IsCandidate = isCandidate;
        AuthenticatorStep = authenticatorStep;
        RecoveryCodeValue = recoveryCodeValue;
    }

    internal string Method { get; }

    /// <summary>Whether the code can be committed. Recovery codes are verified on redemption.</summary>
    internal bool IsCandidate { get; }

    internal long? AuthenticatorStep { get; }

    internal string? RecoveryCodeValue { get; }

    internal static NhSecondFactorCheck Invalid(string method)
    {
        return new NhSecondFactorCheck(method, false, null, null);
    }

    internal static NhSecondFactorCheck Authenticator(long step)
    {
        return new NhSecondFactorCheck(NhTwoFactorMethods.Authenticator, true, step, null);
    }

    internal static NhSecondFactorCheck RecoveryCode(string code)
    {
        return new NhSecondFactorCheck(NhTwoFactorMethods.RecoveryCode, true, null, code);
    }
}
