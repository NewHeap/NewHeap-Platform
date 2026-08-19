using NewHeap.Platform.AspNet.Common.Services.Notification;

namespace SampleProjectManagement.Api.Services;

public static class NotificationProcessingSample
{
    public const int EmailDispatcherConcurrency = 2;

    public static void Configure(NhNotificationSettings options)
    {
        ArgumentNullException.ThrowIfNull(options);

        options.ProcessingMaxRetryAttempts = 3;
        options.ProcessingRetentionPeriod = TimeSpan.FromDays(30);
        options.ProcessingCleanupInterval = TimeSpan.FromHours(1);
        options.ProcessingLockTimeout = TimeSpan.FromMinutes(1);
        options.ProcessingDispatcherConcurrency[NhEmailNotificationDispatcher.DispatcherIdValue]
            = EmailDispatcherConcurrency;
    }
}
