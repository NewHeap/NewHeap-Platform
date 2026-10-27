using System.Net.Mail;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;
using NewHeap.Platform.AspNet.Common.DAL.Entities;
using NewHeap.Platform.AspNet.Common.Models;
using NewHeap.Platform.AspNet.Common.Models.Mutate;
using NewHeap.Platform.AspNet.Common.Services;
using NewHeap.Platform.Common.Models;
using NewHeap.Platform.Common.Services;
using SampleProjectManagement.Api.Composition;

namespace SampleProjectManagement.Api.Services;

public sealed class AccountSampleService
{
    private readonly NhMailService _mailService;
    private readonly INhUserManager<NhUser> _userManager;
    private readonly SampleAccountRecoveryOptions _recovery;

    public AccountSampleService(
        INhUserManager<NhUser> userManager,
        NhMailService mailService,
        IOptions<SampleAccountRecoveryOptions> recovery)
    {
        _userManager = userManager;
        _mailService = mailService;
        _recovery = recovery.Value;
    }

    public async Task<TaskResult> ChangeActiveDivisionAsync(
        Guid userId,
        ChangeActiveDivisionAccountModel model,
        CancellationToken cancellationToken = default)
    {
        var result = await _userManager.ChangeActiviveDivisionAsync(userId, model, cancellationToken);
        return TaskResult.Succeeded(result);
    }

    public async Task<TaskResult> ChangePasswordAsync(
        Guid userId,
        NhChangePasswordUserMutateModel model,
        CancellationToken cancellationToken = default)
    {
        return await _userManager.ChangePasswordAsync(
            userId,
            model,
            userId,
            cancellationToken);
    }

    /// <summary>
    /// Mails the account's owner a link to the reset page with a password-reset token. The link is
    /// built from the configured application origin and reset path only (SPM-075); the request's
    /// reset URL, host and origin are never used, so the token reaches only the application.
    /// </summary>
    public async Task RecoverPasswordAsync(
        NhRecoverPasswordUserMutateModel model,
        CancellationToken cancellationToken = default)
    {
        var user = await _userManager.FindByEmailAsync(model.Email!);
        if (user is null)
        {
            return;
        }

        var token = await _userManager.GeneratePasswordResetTokenAsync(user);
        var resetUrl = QueryHelpers.AddQueryString(
            _recovery.ResetPasswordPage().AbsoluteUri,
            new Dictionary<string, string?>
            {
                ["userId"] = user.Id.ToString(),
                ["token"] = token,
            });
        using var message = new MailMessage
        {
            Subject = "Reset your password",
            Body = $"Open this link to reset your password: {resetUrl}"
        };
        message.To.Add(user.Email!);
        await _mailService.SendAsync(message, cancellationToken: cancellationToken);
    }

    public async Task<TaskResult> ResetPasswordAsync(
        NhResetPasswordUserMutateModel model,
        CancellationToken cancellationToken = default)
    {
        return await _userManager.ResetPasswordAsync(
            model.UserId!.Value,
            model,
            cancellationToken: cancellationToken);
    }
}
