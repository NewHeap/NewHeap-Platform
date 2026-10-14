using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.AspNet.Common.DAL.Entities;
using NewHeap.Platform.AspNet.Common.Models.Mutate;
using NewHeap.Platform.AspNet.Common.Services.Notification;
using NewHeap.Platform.Common.Actors;
using NewHeap.Platform.Common.Models;

namespace NewHeap.Platform.AspNet.Common.Services.Actors;

/// <summary>
/// The default sink: stores messages as NewHeap user notifications when the notifications module
/// is registered, and delivers nothing otherwise.
/// </summary>
internal sealed class NhUserNotificationServiceSink : INhUserNotificationSink
{
    private readonly IServiceProvider _services;

    public NhUserNotificationServiceSink(IServiceProvider services)
    {
        _services = services;
    }

    /// <summary>Whether a NewHeap user-notification store is registered.</summary>
    public bool CanDeliver => _services.GetService<INhUserNotificationService>() is not null;

    public async Task<TaskResult<NhUserNotificationReceipt>> SendAsync(
        NhUserNotificationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var notificationService = _services.GetService<INhUserNotificationService>();
        if (notificationService is null)
        {
            return new TaskResult<NhUserNotificationReceipt>();
        }

        var severity = (NhUserNotificationSeverity)(int)request.Level;
        if (request.ThreadId.HasValue)
        {
            var current = await notificationService.GetAsync(request.ThreadId.Value, cancellationToken);
            if (current is { IsArchived: false })
            {
                var added = await notificationService.AddMessageAsync(
                    current.Id,
                    new NhAddMessageUserNotificationMutateModel
                    {
                        Title = request.Title,
                        Message = request.Message,
                        Severity = severity,
                        Url = request.Url
                    },
                    cancellationToken);
                return added.Success
                    ? TaskResult<NhUserNotificationReceipt>.Succeeded(new NhUserNotificationReceipt(current.Id))
                    : TaskResult<NhUserNotificationReceipt>.Failed(added);
            }
        }

        // A new or archived thread starts again; a group key joins an active thread instead.
        var created = await notificationService.CreateOrAddMessageAsync(
            new NhUserNotificationMutateModel
            {
                UserId = request.UserId,
                Title = request.Title,
                Message = request.Message,
                Url = request.Url,
                UrlInNewTab = request.UrlInNewTab,
                Category = request.Category,
                Severity = severity,
                GroupKey = request.GroupKey
            },
            cancellationToken);
        if (!created.Success)
        {
            return TaskResult<NhUserNotificationReceipt>.Failed(created);
        }

        if (created.Data is null)
        {
            return TaskResult<NhUserNotificationReceipt>.Failed(
                "notification-create-failed",
                "background-operation.notification-create-failed");
        }

        return TaskResult<NhUserNotificationReceipt>.Succeeded(new NhUserNotificationReceipt(created.Data.Id));
    }
}
