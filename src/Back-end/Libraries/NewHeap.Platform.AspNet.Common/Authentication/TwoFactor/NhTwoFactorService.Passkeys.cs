using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NewHeap.Platform.Common.Models;
using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text.Json;

namespace NewHeap.Platform.AspNet.Common.Authentication.TwoFactor;

/// <summary>
/// Passkey ceremonies. The WebAuthn state between the options and the authenticator's
/// response travels in a protected ceremony token, so no server-side session or Identity
/// cookie is needed.
/// </summary>
public partial class NhTwoFactorService<TUser>
{
    private const string DefaultPasskeyName = "Passkey";

    /// <summary>Whether passkeys are enabled and the Identity store can hold them.</summary>
    internal bool PasskeysSupported =>
        _configuration.PasskeysEnabled
        && _passkeyHandler != null
        && _userManager.SupportsUserPasskey;

    public virtual async Task<IReadOnlyList<NhPasskeyViewModel>> GetPasskeysAsync(
        TUser user,
        CancellationToken cancellationToken = default)
    {
        if (!PasskeysSupported)
        {
            return [];
        }

        var passkeys = await _userManager.GetPasskeysAsync(user);
        return passkeys
            .OrderBy(passkey => passkey.CreatedAt)
            .Select(ToViewModel)
            .ToList();
    }

    public virtual async Task<TaskResult<NhPasskeyOptionsResponse>> BeginPasskeyRegistrationAsync(
        TUser user,
        NhTwoFactorReauthentication? reauthentication,
        HttpContext httpContext,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        if (!PasskeysSupported)
        {
            return NhTwoFactorFailureCodes.Fail<NhPasskeyOptionsResponse>(NhTwoFactorFailureCodes.MethodNotAllowed);
        }

        var enrollment = await GetEnrollmentAsync(user, cancellationToken);
        if (enrollment.IsEnrolled)
        {
            var reauthenticationResult = await VerifyReauthenticationAsync(user, reauthentication, cancellationToken);
            if (!reauthenticationResult.Success)
            {
                return TaskResult<NhPasskeyOptionsResponse>.Failed(reauthenticationResult);
            }
        }

        return await CreatePasskeyCreationOptionsAsync(
            user,
            NhTwoFactorTicketPurposes.PasskeyRegistration,
            Convert.ToHexString(RandomNumberGenerator.GetBytes(16)),
            httpContext);
    }

    public virtual Task<TaskResult<NhTwoFactorChangeResult>> CompletePasskeyRegistrationAsync(
        TUser user,
        string ceremonyToken,
        string credentialJson,
        string? name,
        HttpContext httpContext,
        Guid? committedByUserId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        return CompletePasskeyRegistrationCoreAsync(
            user,
            NhTwoFactorTicketPurposes.PasskeyRegistration,
            expectedNonce: null,
            ceremonyToken,
            credentialJson,
            name,
            httpContext,
            committedByUserId,
            cancellationToken);
    }

    public virtual async Task<TaskResult> RenamePasskeyAsync(
        TUser user,
        string passkeyId,
        string name,
        CancellationToken cancellationToken = default)
    {
        if (!PasskeysSupported)
        {
            return NhTwoFactorFailureCodes.Fail(NhTwoFactorFailureCodes.MethodNotAllowed);
        }

        var passkey = await FindPasskeyAsync(user, passkeyId);
        if (passkey == null)
        {
            return NhTwoFactorFailureCodes.Fail(NhTwoFactorFailureCodes.PasskeyNotFound);
        }

        passkey.Name = NormalizePasskeyName(name);

        var result = await _userManager.AddOrUpdatePasskeyAsync(user, passkey);
        if (!result.Succeeded)
        {
            return IdentityFailure(result);
        }

        return TaskResult.Succeeded();
    }

    public virtual async Task<TaskResult<NhTwoFactorChangeResult>> RemovePasskeyAsync(
        TUser user,
        string passkeyId,
        NhTwoFactorReauthentication? reauthentication,
        Guid? committedByUserId,
        CancellationToken cancellationToken = default)
    {
        if (!PasskeysSupported)
        {
            return NhTwoFactorFailureCodes.Fail<NhTwoFactorChangeResult>(NhTwoFactorFailureCodes.MethodNotAllowed);
        }

        var passkey = await FindPasskeyAsync(user, passkeyId);
        if (passkey == null)
        {
            return NhTwoFactorFailureCodes.Fail<NhTwoFactorChangeResult>(NhTwoFactorFailureCodes.PasskeyNotFound);
        }

        var evaluation = await EvaluateAsync(user, NhAuthenticationFactors.Password, cancellationToken);
        var isLastPasskey = evaluation.Enrollment.PasskeyCount <= 1;
        var remainingMethods = UsableMethods(evaluation)
            .Where(method => method != NhTwoFactorMethods.Passkey || !isLastPasskey)
            .ToList();

        if (evaluation.Requirement.EnforcedByPolicy && !remainingMethods.Any(NhTwoFactorMethods.IsPrimary))
        {
            return NhTwoFactorFailureCodes.Fail<NhTwoFactorChangeResult>(NhTwoFactorFailureCodes.RequiredByPolicy);
        }

        var reauthenticationResult = await VerifyReauthenticationAsync(user, reauthentication, cancellationToken);
        if (!reauthenticationResult.Success)
        {
            return TaskResult<NhTwoFactorChangeResult>.Failed(reauthenticationResult);
        }

        // Without another second factor, removing the last passkey disables two-factor
        // authentication, so no unusable recovery codes stay behind.
        var disablesTwoFactor = isLastPasskey
            && !evaluation.Enrollment.Methods.Any(method =>
                method != NhTwoFactorMethods.Passkey && NhTwoFactorMethods.IsPrimary(method));

        var mutationResult = await ExecuteSecurityMutationAsync(
            user,
            async () =>
            {
                if (disablesTwoFactor)
                {
                    var removeFactorsResult = await RemoveSecondFactorsAsync(user, cancellationToken);
                    if (!removeFactorsResult.Success)
                    {
                        return removeFactorsResult;
                    }

                    return await NotifyAsync(user, NhTwoFactorSecurityEvent.Disabled, cancellationToken: cancellationToken);
                }

                var removeResult = await _userManager.RemovePasskeyAsync(user, passkey.CredentialId);
                if (!removeResult.Succeeded)
                {
                    return IdentityFailure(removeResult);
                }

                return await NotifyAsync(user, NhTwoFactorSecurityEvent.PasskeyRemoved, cancellationToken: cancellationToken);
            },
            disablesTwoFactor
                ? "Two-factor authentication disabled by removing the last passkey."
                : "Passkey removed.",
            committedByUserId,
            cancellationToken);

        if (!mutationResult.Success)
        {
            return TaskResult<NhTwoFactorChangeResult>.Failed(mutationResult);
        }

        return new NhTwoFactorChangeResult(null, new NhAuthenticationProof(user.Id, [NhAuthenticationFactors.Password]));
    }

    /// <summary>
    /// Creates passkey creation options for a user who enrolls during sign-in. The ceremony
    /// is bound to the enrollment.
    /// </summary>
    internal async Task<TaskResult<NhPasskeyOptionsResponse>> BeginEnrollmentPasskeyAsync(
        TUser user,
        NhTwoFactorTicket enrollment,
        HttpContext httpContext)
    {
        if (!PasskeysSupported
            || !_configuration.EnrollableMethods(requiredByPolicy: true).Contains(NhTwoFactorMethods.Passkey))
        {
            return NhTwoFactorFailureCodes.Fail<NhPasskeyOptionsResponse>(NhTwoFactorFailureCodes.MethodNotAllowed);
        }

        return await CreatePasskeyCreationOptionsAsync(
            user,
            NhTwoFactorTicketPurposes.PasskeyEnrollment,
            enrollment.Nonce,
            httpContext);
    }

    internal Task<TaskResult<NhTwoFactorChangeResult>> CompleteEnrollmentPasskeyAsync(
        TUser user,
        NhTwoFactorTicket enrollment,
        string? ceremonyToken,
        string? credentialJson,
        string? name,
        HttpContext httpContext)
    {
        return CompletePasskeyRegistrationCoreAsync(
            user,
            NhTwoFactorTicketPurposes.PasskeyEnrollment,
            enrollment.Nonce,
            ceremonyToken,
            credentialJson,
            name,
            httpContext,
            user.Id,
            CancellationToken.None);
    }

    /// <summary>
    /// Creates passkey request options for a pending challenge. The ceremony is bound to the
    /// challenge, so the assertion cannot complete another sign-in.
    /// </summary>
    internal async Task<TaskResult<NhPasskeyOptionsResponse>> BeginPasskeyAssertionAsync(
        TUser user,
        NhTwoFactorTicket challenge,
        IReadOnlyList<string> allowedMethods,
        HttpContext httpContext)
    {
        if (!PasskeysSupported || !allowedMethods.Contains(NhTwoFactorMethods.Passkey))
        {
            return NhTwoFactorFailureCodes.Fail<NhPasskeyOptionsResponse>(NhTwoFactorFailureCodes.MethodNotAllowed);
        }

        var options = await _passkeyHandler!.MakeRequestOptionsAsync(user, httpContext);
        var ticket = new NhTwoFactorTicket(
            user.Id,
            challenge.SecurityStamp,
            NhTwoFactorTicketPurposes.PasskeyAssertion,
            challenge.Factor,
            challenge.Nonce,
            challenge.ExpiresAt,
            options.AssertionState);

        return CreateOptionsResponse(options.RequestOptionsJson, ticket);
    }

    /// <summary>
    /// Verifies a passkey assertion for a pending challenge without writing. The new
    /// signature counter is stored in <see cref="CommitSecondFactorAsync"/>.
    /// </summary>
    internal async Task<NhSecondFactorCheck> CheckPasskeyAssertionAsync(
        TUser user,
        NhTwoFactorTicket challenge,
        string? ceremonyToken,
        string? credentialJson,
        HttpContext httpContext)
    {
        if (!PasskeysSupported || string.IsNullOrWhiteSpace(credentialJson))
        {
            return NhSecondFactorCheck.Invalid(NhTwoFactorMethods.Passkey);
        }

        var ticket = _ticketProtector.Unprotect(ceremonyToken, NhTwoFactorTicketPurposes.PasskeyAssertion);
        if (ticket == null
            || ticket.UserId != user.Id
            || ticket.Nonce != challenge.Nonce
            || string.IsNullOrEmpty(ticket.State))
        {
            return NhSecondFactorCheck.Invalid(NhTwoFactorMethods.Passkey);
        }

        var assertion = await _passkeyHandler!.PerformAssertionAsync(new PasskeyAssertionContext
        {
            HttpContext = httpContext,
            CredentialJson = credentialJson,
            AssertionState = ticket.State,
        });

        if (!assertion.Succeeded || assertion.User?.Id != user.Id)
        {
            _logger.LogInformation(
                "Passkey assertion failed for user {UserId}: {Reason}",
                user.Id,
                assertion.Failure?.Message);
            return NhSecondFactorCheck.Invalid(NhTwoFactorMethods.Passkey);
        }

        return NhSecondFactorCheck.Passkey(assertion.Passkey!);
    }

    /// <summary>
    /// Creates request options for a passwordless sign-in with a discoverable passkey. The
    /// options do not name a user, so they reveal nothing about registered accounts.
    /// </summary>
    internal async Task<TaskResult<NhPasskeyOptionsResponse>> BeginPasskeySignInAsync(HttpContext httpContext)
    {
        if (!PasskeysSupported)
        {
            return NhTwoFactorFailureCodes.Fail<NhPasskeyOptionsResponse>(NhTwoFactorFailureCodes.MethodNotAllowed);
        }

        var options = await _passkeyHandler!.MakeRequestOptionsAsync(null, httpContext);
        var ticket = new NhTwoFactorTicket(
            Guid.Empty,
            string.Empty,
            NhTwoFactorTicketPurposes.PasskeySignIn,
            NhAuthenticationFactors.Passkey,
            Convert.ToHexString(RandomNumberGenerator.GetBytes(16)),
            _timeProvider.GetUtcNow().Add(_configuration.ChallengeLifetime),
            options.AssertionState);

        return CreateOptionsResponse(options.RequestOptionsJson, ticket);
    }

    /// <summary>
    /// Verifies a passwordless passkey assertion, consumes its ceremony and stores the new
    /// signature counter. Returns the user whom the passkey belongs to.
    /// </summary>
    internal async Task<TaskResult<TUser>> VerifyPasskeySignInAsync(
        string? ceremonyToken,
        string? credentialJson,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        if (!PasskeysSupported)
        {
            return NhTwoFactorFailureCodes.Fail<TUser>(NhTwoFactorFailureCodes.MethodNotAllowed);
        }

        var ticket = _ticketProtector.Unprotect(ceremonyToken, NhTwoFactorTicketPurposes.PasskeySignIn);
        if (ticket == null || string.IsNullOrEmpty(ticket.State))
        {
            return NhTwoFactorFailureCodes.Fail<TUser>(NhTwoFactorFailureCodes.ChallengeExpired);
        }

        if (string.IsNullOrWhiteSpace(credentialJson))
        {
            return NhTwoFactorFailureCodes.Fail<TUser>(NhTwoFactorFailureCodes.PasskeyInvalid);
        }

        var assertion = await _passkeyHandler!.PerformAssertionAsync(new PasskeyAssertionContext
        {
            HttpContext = httpContext,
            CredentialJson = credentialJson,
            AssertionState = ticket.State,
        });

        if (!assertion.Succeeded || assertion.User == null)
        {
            _logger.LogInformation("Passkey sign-in failed: {Reason}", assertion.Failure?.Message);
            return NhTwoFactorFailureCodes.Fail<TUser>(NhTwoFactorFailureCodes.PasskeyInvalid);
        }

        var user = assertion.User;
        var repository = _nhUserManager.GetRepository();
        await using var transaction = await repository.StartOrGetTransactionScopeAsync(cancellationToken);

        if (!await TryConsumeChallengeAsync(user, ticket))
        {
            await transaction.RollbackAsync(cancellationToken);
            return NhTwoFactorFailureCodes.Fail<TUser>(NhTwoFactorFailureCodes.ChallengeExpired);
        }

        var storeResult = await StorePasskeyUseAsync(user, assertion.Passkey!);
        if (!storeResult.Success)
        {
            await transaction.RollbackAsync(cancellationToken);
            return TaskResult<TUser>.Failed(storeResult);
        }

        await transaction.CommitAsync(cancellationToken);
        return user;
    }

    private async Task<int> CountPasskeysAsync(TUser user)
    {
        if (!PasskeysSupported)
        {
            return 0;
        }

        return (await _userManager.GetPasskeysAsync(user)).Count;
    }

    private async Task<TaskResult<NhPasskeyOptionsResponse>> CreatePasskeyCreationOptionsAsync(
        TUser user,
        string purpose,
        string nonce,
        HttpContext httpContext)
    {
        var securityStamp = await _userManager.GetSecurityStampAsync(user);
        if (string.IsNullOrEmpty(securityStamp))
        {
            throw new InvalidOperationException("The user does not have a security stamp.");
        }

        var userId = await _userManager.GetUserIdAsync(user);
        var accountName = await _userManager.GetEmailAsync(user)
            ?? await _userManager.GetUserNameAsync(user)
            ?? userId;

        var options = await _passkeyHandler!.MakeCreationOptionsAsync(
            new PasskeyUserEntity
            {
                Id = userId,
                Name = accountName,
                DisplayName = accountName,
            },
            httpContext);

        var ticket = new NhTwoFactorTicket(
            user.Id,
            securityStamp,
            purpose,
            NhTwoFactorMethods.Passkey,
            nonce,
            _timeProvider.GetUtcNow().Add(_configuration.ChallengeLifetime),
            options.AttestationState);

        return CreateOptionsResponse(options.CreationOptionsJson, ticket);
    }

    private async Task<TaskResult<NhTwoFactorChangeResult>> CompletePasskeyRegistrationCoreAsync(
        TUser user,
        string purpose,
        string? expectedNonce,
        string? ceremonyToken,
        string? credentialJson,
        string? name,
        HttpContext httpContext,
        Guid? committedByUserId,
        CancellationToken cancellationToken)
    {
        if (!PasskeysSupported)
        {
            return NhTwoFactorFailureCodes.Fail<NhTwoFactorChangeResult>(NhTwoFactorFailureCodes.MethodNotAllowed);
        }

        // The ticket carries the security stamp, and the registration changes the stamp, so
        // a ceremony token completes at most one registration.
        var ticket = _ticketProtector.Unprotect(ceremonyToken, purpose);
        if (ticket == null
            || ticket.UserId != user.Id
            || string.IsNullOrEmpty(ticket.State)
            || (expectedNonce != null && ticket.Nonce != expectedNonce)
            || !string.Equals(await _userManager.GetSecurityStampAsync(user), ticket.SecurityStamp, StringComparison.Ordinal))
        {
            return NhTwoFactorFailureCodes.Fail<NhTwoFactorChangeResult>(NhTwoFactorFailureCodes.ChallengeExpired);
        }

        if (string.IsNullOrWhiteSpace(credentialJson))
        {
            return NhTwoFactorFailureCodes.Fail<NhTwoFactorChangeResult>(NhTwoFactorFailureCodes.PasskeyInvalid);
        }

        var attestation = await _passkeyHandler!.PerformAttestationAsync(new PasskeyAttestationContext
        {
            HttpContext = httpContext,
            CredentialJson = credentialJson,
            AttestationState = ticket.State,
        });

        var userId = await _userManager.GetUserIdAsync(user);
        if (!attestation.Succeeded || attestation.UserEntity?.Id != userId)
        {
            _logger.LogInformation(
                "Passkey registration failed for user {UserId}: {Reason}",
                user.Id,
                attestation.Failure?.Message);
            return NhTwoFactorFailureCodes.Fail<NhTwoFactorChangeResult>(NhTwoFactorFailureCodes.PasskeyInvalid);
        }

        var passkey = attestation.Passkey!;
        passkey.Name = NormalizePasskeyName(name);

        var wasEnrolled = (await GetEnrollmentAsync(user, cancellationToken)).IsEnrolled;
        IReadOnlyList<string>? recoveryCodes = null;

        var mutationResult = await ExecuteSecurityMutationAsync(
            user,
            async () =>
            {
                var addResult = await _userManager.AddOrUpdatePasskeyAsync(user, passkey);
                if (!addResult.Succeeded)
                {
                    return IdentityFailure(addResult);
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
                    wasEnrolled ? NhTwoFactorSecurityEvent.PasskeyAdded : NhTwoFactorSecurityEvent.Enabled,
                    cancellationToken: cancellationToken);
            },
            wasEnrolled ? "Passkey added." : "Two-factor authentication enabled with a passkey.",
            committedByUserId,
            cancellationToken);

        if (!mutationResult.Success)
        {
            return TaskResult<NhTwoFactorChangeResult>.Failed(mutationResult);
        }

        return new NhTwoFactorChangeResult(recoveryCodes, new NhAuthenticationProof(user.Id, [NhTwoFactorMethods.Passkey]));
    }

    /// <summary>
    /// Stores the signature counter of a used passkey. The write updates the user's
    /// concurrency stamp, so of two concurrent uses only one commits.
    /// </summary>
    private async Task<TaskResult> StorePasskeyUseAsync(TUser user, UserPasskeyInfo passkey)
    {
        try
        {
            var result = await _userManager.AddOrUpdatePasskeyAsync(user, passkey);
            if (!result.Succeeded)
            {
                return NhTwoFactorFailureCodes.Fail(NhTwoFactorFailureCodes.ChallengeExpired);
            }
        }
        catch (DbUpdateException)
        {
            return NhTwoFactorFailureCodes.Fail(NhTwoFactorFailureCodes.ChallengeExpired);
        }

        return TaskResult.Succeeded();
    }

    private async Task<TaskResult> RemoveAllPasskeysAsync(TUser user)
    {
        if (!PasskeysSupported)
        {
            return TaskResult.Succeeded();
        }

        foreach (var passkey in await _userManager.GetPasskeysAsync(user))
        {
            var result = await _userManager.RemovePasskeyAsync(user, passkey.CredentialId);
            if (!result.Succeeded)
            {
                return IdentityFailure(result);
            }
        }

        return TaskResult.Succeeded();
    }

    private async Task<UserPasskeyInfo?> FindPasskeyAsync(TUser user, string? passkeyId)
    {
        if (string.IsNullOrWhiteSpace(passkeyId))
        {
            return null;
        }

        byte[] credentialId;
        try
        {
            credentialId = Base64Url.DecodeFromChars(passkeyId);
        }
        catch (FormatException)
        {
            return null;
        }

        return await _userManager.GetPasskeyAsync(user, credentialId);
    }

    private TaskResult<NhPasskeyOptionsResponse> CreateOptionsResponse(string optionsJson, NhTwoFactorTicket ticket)
    {
        using var document = JsonDocument.Parse(optionsJson);

        return new NhPasskeyOptionsResponse
        {
            Options = document.RootElement.Clone(),
            CeremonyToken = _ticketProtector.Protect(ticket),
            ExpiresAt = ticket.ExpiresAt,
        };
    }

    private static NhPasskeyViewModel ToViewModel(UserPasskeyInfo passkey)
    {
        return new NhPasskeyViewModel
        {
            Id = Base64Url.EncodeToString(passkey.CredentialId),
            Name = string.IsNullOrWhiteSpace(passkey.Name) ? DefaultPasskeyName : passkey.Name,
            CreatedAt = passkey.CreatedAt,
            IsBackedUp = passkey.IsBackedUp,
            Transports = passkey.Transports ?? [],
        };
    }

    private static string NormalizePasskeyName(string? name)
    {
        var trimmed = name?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return DefaultPasskeyName;
        }

        return trimmed.Length > NhPasskeyViewModel.MaxNameLength
            ? trimmed[..NhPasskeyViewModel.MaxNameLength]
            : trimmed;
    }
}
