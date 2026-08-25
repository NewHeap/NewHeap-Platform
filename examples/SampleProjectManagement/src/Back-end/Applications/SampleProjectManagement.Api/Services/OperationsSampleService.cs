using System.Net.Mail;
using NewHeap.Platform.AspNet.Common.DAL.Entities;
using NewHeap.Platform.AspNet.Common.Models.Mutate;
using NewHeap.Platform.AspNet.Common.Services;
using NewHeap.Platform.AspNet.Common.Services.Notification;
using NewHeap.Platform.AspNet.Common.Services.BackgroundOperations;
using NewHeap.Platform.Common.Models;
using NewHeap.Platform.Common.Services;
using NewHeap.Platform.Common.Utilities;
using SampleProjectManagement.Api.Jobs;
using SampleProjectManagement.Api.Models;

namespace SampleProjectManagement.Api.Services;

public sealed class OperationsSampleService
{
    private const string RecurringJobId = "sample-project-management-overdue";
    private readonly NhMailService _mailService;
    private readonly INhNotificationService _notificationService;
    private readonly RazorViewService _razorViewService;
    private readonly INhBackgroundOperationService _backgroundOperations;

    public OperationsSampleService(
        NhMailService mailService,
        INhNotificationService notificationService,
        RazorViewService razorViewService,
        INhBackgroundOperationService backgroundOperations)
    {
        _mailService = mailService;
        _notificationService = notificationService;
        _razorViewService = razorViewService;
        _backgroundOperations = backgroundOperations;
    }

    public JobSampleResult EnqueueOverdueJob()
    {
        var jobId = NhHangfireUtil.BackgroundJob.Enqueue<ProjectMaintenanceJob>(
            job => job.RecalculateOverdueAsync());
        return new JobSampleResult(jobId, NhHangfireUtil.GetQueueName());
    }

    public JobSampleResult ScheduleDraftCleanup()
    {
        var cutoff = DateTimeOffset.UtcNow.AddDays(-30);
        var jobId = NhHangfireUtil.BackgroundJob.Schedule<ProjectMaintenanceJob>(
            job => job.DeleteAbandonedDraftsAsync(cutoff),
            TimeSpan.FromMinutes(5));
        return new JobSampleResult(jobId, NhHangfireUtil.GetQueueName(), cutoff);
    }

    public string RegisterRecurringJob()
    {
        NhHangfireUtil.RecurringJob.AddOrUpdate<ProjectMaintenanceJob>(
            RecurringJobId,
            job => job.RecalculateOverdueAsync(),
            "0 */6 * * *");
        return RecurringJobId;
    }

    public async Task SendMailAsync(
        SendSampleMailMutateModel model,
        CancellationToken cancellationToken = default)
    {
        var body = await _razorViewService.RenderViewToStringAsync(
            "Mail/ProjectAssignment",
            new ProjectAssignmentMailViewModel
            {
                Language = "nl",
                Title = "Project toegewezen",
                Introduction = "You have been assigned to the following project:",
                ProjectKey = model.ProjectKey,
                ProjectName = model.ProjectName,
                ProjectUrl = model.ProjectUrl,
                ActionLabel = "Open project"
            });
        using var message = new MailMessage
        {
            Subject = $"Project {model.ProjectKey} toegewezen",
            Body = body,
            IsBodyHtml = true
        };
        message.To.Add(model.To);
        await _mailService.SendAsync(message, cancellationToken: cancellationToken);
    }

    public Task<TaskResult<NhNotification>> CreateNotificationAsync(
        CreateSampleNotificationMutateModel model,
        CancellationToken cancellationToken = default)
    {
        var notification = NhNotificationBuilder
            .Create($"Project {model.ProjectKey} assignment")
            .WithPriority(NhNotificationPriority.Normal)
            .WithUserNotificationDelivery(new NhUserNotificationDeliveryData
            {
                Notification = new NhUserNotificationMutateModel
                {
                    UserId = model.UserId,
                    Title = $"Project {model.ProjectKey}",
                    Message = model.Message,
                    Url = $"/projects/{model.ProjectId}",
                    UrlInNewTab = false
                }
            })
            .WithEmailDelivery(new NhEmailDeliveryData
            {
                To = [model.Email],
                Subject = $"Project {model.ProjectKey}",
                Body = model.Message,
                IsBodyHtml = false
            })
            .Build();

        return _notificationService.CreateAsync(notification, cancellationToken);
    }

    public Task<TaskResult<NewHeap.Platform.AspNet.Common.Models.View.NhBackgroundOperationViewModel>>
        EnqueuePortfolioAnalysisAsync(
            ProjectPortfolioAnalysisMutateModel model,
            Guid ownerUserId,
            Guid divisionId,
            CancellationToken cancellationToken = default)
    {
        return _backgroundOperations.EnqueueAsync(
            new ProjectPortfolioAnalysisRequest(
                divisionId,
                model.Passes,
                model.DelayPerItemMilliseconds,
                model.FailFirstAttempt),
            new NhBackgroundOperationEnqueueOptions
            {
                OwnerUserId = ownerUserId,
                DivisionId = divisionId,
                IdempotencyKey = model.IdempotencyKey,
                DomainObjectType = "division",
                DomainObjectId = divisionId.ToString("N")
            },
            cancellationToken);
    }
}
