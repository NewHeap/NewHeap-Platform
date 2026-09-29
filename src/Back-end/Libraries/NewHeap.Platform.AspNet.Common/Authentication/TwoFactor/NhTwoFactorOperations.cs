using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using NewHeap.Platform.AspNet.Common.Services;
using NewHeap.Platform.AspNet.Common.Services.BackgroundOperations;
using NewHeap.Platform.Common.Models;

namespace NewHeap.Platform.AspNet.Common.Authentication.TwoFactor;

/// <summary>
/// Reminds every user whom the two-factor policy requires to enroll a second factor.
/// </summary>
public sealed record NhTwoFactorEnrollmentReminderRequest(Guid RequestedByUserId);

/// <summary>
/// Ends the sessions of every user whom the two-factor policy requires to enroll, so they
/// enroll at their next sign-in instead of continuing with an existing session.
/// </summary>
public sealed record NhTwoFactorSessionRevocationRequest(Guid RequestedByUserId);

/// <summary>
/// Registers the two-factor administration operations on the background-operation builder.
/// </summary>
public static class NhTwoFactorBackgroundOperationExtensions
{
    public const string EnrollmentReminderOperationType = "nh-two-factor-enrollment-reminders";
    public const string SessionRevocationOperationType = "nh-two-factor-session-revocation";

    /// <summary>
    /// Registers the operations behind the two-factor administration endpoints: enrollment
    /// reminders and ending the sessions of users who must enroll.
    /// </summary>
    public static NhBackgroundOperationBuilder AddTwoFactorOperations<TUser>(this NhBackgroundOperationBuilder builder)
        where TUser : IdentityUser<Guid>
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Add<NhTwoFactorEnrollmentReminderRequest, NhTwoFactorEnrollmentReminderOperation<TUser>>(
            EnrollmentReminderOperationType);
        builder.Add<NhTwoFactorSessionRevocationRequest, NhTwoFactorSessionRevocationOperation<TUser>>(
            SessionRevocationOperationType);

        return builder;
    }
}

/// <summary>
/// Notifies every user whom the policy requires to enroll a second factor.
/// </summary>
public sealed class NhTwoFactorEnrollmentReminderOperation<TUser> : INhBackgroundOperationHandler<NhTwoFactorEnrollmentReminderRequest>
    where TUser : IdentityUser<Guid>
{
    private readonly NhTwoFactorService<TUser> _twoFactor;
    private readonly UserManager<TUser> _userManager;
    private readonly INhUserManager<TUser> _nhUserManager;

    public NhTwoFactorEnrollmentReminderOperation(
        NhTwoFactorService<TUser> twoFactor,
        UserManager<TUser> userManager,
        INhUserManager<TUser> nhUserManager)
    {
        _twoFactor = twoFactor;
        _userManager = userManager;
        _nhUserManager = nhUserManager;
    }

    public Task<TaskResult> ExecuteAsync(
        NhTwoFactorEnrollmentReminderRequest request,
        INhBackgroundOperationContext context,
        CancellationToken cancellationToken)
    {
        return NhTwoFactorPolicySweep.RunAsync(
            _twoFactor,
            _userManager,
            _nhUserManager,
            context,
            (user, token) => _twoFactor.SendEnrollmentReminderAsync(user, token),
            cancellationToken);
    }
}

/// <summary>
/// Ends every session of the users whom the policy requires to enroll a second factor.
/// </summary>
public sealed class NhTwoFactorSessionRevocationOperation<TUser> : INhBackgroundOperationHandler<NhTwoFactorSessionRevocationRequest>
    where TUser : IdentityUser<Guid>
{
    private readonly NhTwoFactorService<TUser> _twoFactor;
    private readonly UserManager<TUser> _userManager;
    private readonly INhUserManager<TUser> _nhUserManager;

    public NhTwoFactorSessionRevocationOperation(
        NhTwoFactorService<TUser> twoFactor,
        UserManager<TUser> userManager,
        INhUserManager<TUser> nhUserManager)
    {
        _twoFactor = twoFactor;
        _userManager = userManager;
        _nhUserManager = nhUserManager;
    }

    public Task<TaskResult> ExecuteAsync(
        NhTwoFactorSessionRevocationRequest request,
        INhBackgroundOperationContext context,
        CancellationToken cancellationToken)
    {
        return NhTwoFactorPolicySweep.RunAsync(
            _twoFactor,
            _userManager,
            _nhUserManager,
            context,
            (user, token) => _twoFactor.EndSessionsAsync(user, request.RequestedByUserId, token),
            cancellationToken);
    }
}

/// <summary>
/// Visits every user in batches and runs an action for the users whom the policy requires
/// to enroll a second factor.
/// </summary>
internal static class NhTwoFactorPolicySweep
{
    private const int BatchSize = 100;

    internal static async Task<TaskResult> RunAsync<TUser>(
        NhTwoFactorService<TUser> twoFactor,
        UserManager<TUser> userManager,
        INhUserManager<TUser> nhUserManager,
        INhBackgroundOperationContext context,
        Func<TUser, CancellationToken, Task<TaskResult>> action,
        CancellationToken cancellationToken)
        where TUser : IdentityUser<Guid>
    {
        var total = await userManager.Users.CountAsync(cancellationToken);
        var processed = 0;
        await context.Progress.ReportAsync(0, total, cancellationToken: cancellationToken);

        for (var offset = 0; offset < total; offset += BatchSize)
        {
            var userIds = await userManager.Users
                .OrderBy(user => user.Id)
                .Skip(offset)
                .Take(BatchSize)
                .Select(user => user.Id)
                .ToListAsync(cancellationToken);

            foreach (var userId in userIds)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var user = await userManager.FindByIdAsync(userId.ToString());
                if (user != null)
                {
                    var evaluation = await twoFactor.EvaluateAsync(user, NhAuthenticationFactors.Password, cancellationToken);
                    var mustEnroll = evaluation.Requirement.EnforcedByPolicy
                        && NhTwoFactorService<TUser>.UsableMethods(evaluation).Count == 0;

                    if (mustEnroll)
                    {
                        var result = await action(user, cancellationToken);
                        if (!result.Success)
                        {
                            return result;
                        }
                    }
                }

                processed++;
            }

            // Keep the change tracker small while visiting a large user base.
            nhUserManager.GetRepository().ClearTracking();
            await context.Progress.ReportAsync(processed, total, cancellationToken: cancellationToken);
        }

        return TaskResult.Succeeded();
    }
}
