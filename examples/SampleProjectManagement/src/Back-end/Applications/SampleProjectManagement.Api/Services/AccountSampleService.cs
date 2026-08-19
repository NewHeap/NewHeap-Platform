using System.Net;
using System.Net.Mail;
using NewHeap.Platform.AspNet.Common.DAL.Entities;
using NewHeap.Platform.AspNet.Common.Models;
using NewHeap.Platform.AspNet.Common.Models.Mutate;
using NewHeap.Platform.AspNet.Common.Services;
using NewHeap.Platform.Common.Models;
using NewHeap.Platform.Common.Services;

namespace SampleProjectManagement.Api.Services;

public sealed class AccountSampleService
{
    private readonly NhMailService _mailService;
    private readonly INhUserManager<NhUser> _userManager;

    public AccountSampleService(
        INhUserManager<NhUser> userManager,
        NhMailService mailService)
    {
        _userManager = userManager;
        _mailService = mailService;
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
        var result = await _userManager.ChangePasswordAsync(
            userId,
            model,
            userId,
            cancellationToken);
        return TaskResult.Succeeded(result);
    }

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
        var resetUrl = $"{model.ResetUrl}?userId={user.Id}&token={WebUtility.UrlEncode(token)}";
        using var message = new MailMessage
        {
            Subject = "Wachtwoord herstellen",
            Body = $"Open this link to reset your password: {resetUrl}"
        };
        message.To.Add(model.Email!);
        await _mailService.SendAsync(message, cancellationToken: cancellationToken);
    }

    public async Task<TaskResult> ResetPasswordAsync(
        NhResetPasswordUserMutateModel model,
        CancellationToken cancellationToken = default)
    {
        var result = await _userManager.ResetPasswordAsync(
            model.UserId!.Value,
            model,
            cancellationToken: cancellationToken);
        return TaskResult.Succeeded(result);
    }
}
