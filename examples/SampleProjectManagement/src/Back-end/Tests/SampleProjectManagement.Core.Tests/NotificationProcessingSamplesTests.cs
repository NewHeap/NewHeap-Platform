using NewHeap.Platform.AspNet.Common.Services.Notification;
using SampleProjectManagement.Api.Services;
using Xunit;

namespace SampleProjectManagement.Core.Tests;

public sealed class NotificationProcessingSamplesTests
{
    [Fact]
    public void DispatcherConcurrencyIsAnExplicitPerChannelOptIn()
    {
        var settings = new NhNotificationSettings();

        NotificationProcessingSample.Configure(settings);

        Assert.Equal(
            NotificationProcessingSample.EmailDispatcherConcurrency,
            settings.ProcessingDispatcherConcurrency[NhEmailNotificationDispatcher.DispatcherIdValue]);
        Assert.DoesNotContain(
            NhUserNotificaitonNotificationDispatcher.DispatcherIdValue,
            settings.ProcessingDispatcherConcurrency.Keys);
    }
}
