using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NewHeap.Platform.AspNet.Common.DAL;
using NewHeap.Platform.AspNet.Common.DAL.Entities;
using NewHeap.Platform.AspNet.Common.Services.Notification;
using NewHeap.Platform.Common.Models;
using NewHeap.Platform.Tests.Relational;
using Xunit;

namespace NewHeap.Platform.AspNet.Common.Tests;

/// <summary>
/// How long the notification processor keeps the data of a delivery, short-lived content
/// (<see cref="INhExpiringDeliveryData"/>) included, with the notification model that applications
/// map through <c>AddNewHeapNotifications</c>.
/// </summary>
public sealed class NhNotificationDeliveryRetentionProviderTests
{
    [Theory]
    [InlineData(RelationalTestProvider.SqlServer)]
    [InlineData(RelationalTestProvider.PostgreSql)]
    public async Task RetentionCleanupClearsEveryFinishedDeliveryOnEachRelationalProvider(RelationalTestProvider provider)
    {
        await using var host = await DeliveryHost.CreateAsync(provider);
        var now = DateTimeOffset.UtcNow;
        var old = now.AddDays(-40);
        var recent = now.AddDays(-1);

        // Finished longer than the retention period ago: every terminal status loses its data.
        var oldSucceeded = await host.AddDeliveryAsync(
            NotificationDeliveryStatus.Succeeded, scheduledAt: old, lastSendAttemptAt: old, sentAt: old);
        var oldFailed = await host.AddDeliveryAsync(
            NotificationDeliveryStatus.Failed, scheduledAt: old, lastSendAttemptAt: old);
        var oldCancelledWithoutAttempt = await host.AddDeliveryAsync(
            NotificationDeliveryStatus.Cancelled, scheduledAt: old, lastModifiedAt: old);

        // A failed or cancelled delivery counts from its last attempt, a succeeded one from sending.
        var cancelledAfterRecentAttempt = await host.AddDeliveryAsync(
            NotificationDeliveryStatus.Cancelled, scheduledAt: old, lastSendAttemptAt: recent, lastModifiedAt: old);
        var recentFailed = await host.AddDeliveryAsync(
            NotificationDeliveryStatus.Failed, scheduledAt: old, lastSendAttemptAt: recent);
        var recentlySent = await host.AddDeliveryAsync(
            NotificationDeliveryStatus.Succeeded, scheduledAt: old, lastSendAttemptAt: recent, sentAt: recent);

        // Work that has not finished keeps its data, however old it is.
        var oldQueued = await host.AddDeliveryAsync(
            NotificationDeliveryStatus.Queued, scheduledAt: old, lastModifiedAt: old);
        var oldProcessing = await host.AddDeliveryAsync(
            NotificationDeliveryStatus.Processing, scheduledAt: old, lastSendAttemptAt: old);

        await host.Processor.CleanupFinishedDeliveriesAsync(CancellationToken.None);

        var deliveries = await host.GetDeliveriesAsync();
        foreach (var cleaned in new[] { oldSucceeded, oldFailed, oldCancelledWithoutAttempt })
        {
            Assert.True(deliveries[cleaned].IsCleaned, host.Label);
            Assert.Null(deliveries[cleaned].Data);
        }

        foreach (var kept in new[] { cancelledAfterRecentAttempt, recentFailed, recentlySent, oldQueued, oldProcessing })
        {
            Assert.False(deliveries[kept].IsCleaned, host.Label);
            Assert.NotNull(deliveries[kept].Data);
        }
    }

    [Theory]
    [InlineData(RelationalTestProvider.SqlServer)]
    [InlineData(RelationalTestProvider.PostgreSql)]
    public async Task ExpiringContentLeavesTheStoreOnceTheDeliveryEndsOnEachRelationalProvider(RelationalTestProvider provider)
    {
        await using var host = await DeliveryHost.CreateAsync(
            provider,
            settings => settings.ProcessingMaxRetryAttempts = 1);
        var now = DateTimeOffset.UtcNow;
        var codeExpiresAt = now.AddMinutes(10);

        // Claimed in this order: by scheduled time, as they have the same priority.
        var expiredBeforeDispatch = await host.AddDeliveryAsync(
            NotificationDeliveryStatus.Queued, scheduledAt: now.AddMinutes(-5), expiresAt: now.AddMinutes(-1));
        var sent = await host.AddDeliveryAsync(
            NotificationDeliveryStatus.Queued, scheduledAt: now.AddMinutes(-4), expiresAt: codeExpiresAt);
        var failed = await host.AddDeliveryAsync(
            NotificationDeliveryStatus.Queued, scheduledAt: now.AddMinutes(-3), expiresAt: codeExpiresAt);
        var withoutExpiry = await host.AddDeliveryAsync(
            NotificationDeliveryStatus.Queued, scheduledAt: now.AddMinutes(-2));

        // Scheduled later, so no claim reaches them before the cleanup pass does.
        var expiredWhileWaiting = await host.AddDeliveryAsync(
            NotificationDeliveryStatus.Queued, scheduledAt: now.AddHours(1), expiresAt: now.AddMinutes(-1));
        var stillValidWhileWaiting = await host.AddDeliveryAsync(
            NotificationDeliveryStatus.Queued, scheduledAt: now.AddHours(1), expiresAt: codeExpiresAt);

        // A claim cancels expired content instead of sending it and takes the next delivery.
        var sentClaim = await host.Processor.TryClaimNextDeliveryAsync(
            NhEmailNotificationDispatcher.DispatcherIdValue,
            CancellationToken.None);
        Assert.Equal(sent, sentClaim?.DeliveryId);

        var deliveries = await host.GetDeliveriesAsync();
        Assert.Equal(NotificationDeliveryStatus.Cancelled, deliveries[expiredBeforeDispatch].Status);
        Assert.Equal(
            NhNotificationProcessingService.ExpiredBeforeDispatchMessage,
            deliveries[expiredBeforeDispatch].LastFailedMessage);
        Assert.Equal(0, deliveries[expiredBeforeDispatch].AttemptCount);
        AssertDiscarded(deliveries[expiredBeforeDispatch], host.Label);

        // While the delivery is being sent its content is still there; once sent, it is gone.
        Assert.NotNull(deliveries[sent].Data);
        await host.Processor.CompleteDeliveryAttemptAsync(
            NhEmailNotificationDispatcher.DispatcherIdValue,
            sentClaim!,
            TaskResult.Succeeded(),
            CancellationToken.None);

        deliveries = await host.GetDeliveriesAsync();
        Assert.Equal(NotificationDeliveryStatus.Succeeded, deliveries[sent].Status);
        Assert.NotNull(deliveries[sent].SentAt);
        AssertDiscarded(deliveries[sent], host.Label);

        // A delivery that fails for good loses its content as well.
        var failedClaim = await host.Processor.TryClaimNextDeliveryAsync(
            NhEmailNotificationDispatcher.DispatcherIdValue,
            CancellationToken.None);
        Assert.Equal(failed, failedClaim?.DeliveryId);
        var failure = new TaskResult();
        failure.AddError("DispatchError", "The mail server refused the message.");
        await host.Processor.CompleteDeliveryAttemptAsync(
            NhEmailNotificationDispatcher.DispatcherIdValue,
            failedClaim!,
            failure,
            CancellationToken.None);

        deliveries = await host.GetDeliveriesAsync();
        Assert.Equal(NotificationDeliveryStatus.Failed, deliveries[failed].Status);
        AssertDiscarded(deliveries[failed], host.Label);

        // Content without an expiry follows the retention period.
        var regularClaim = await host.Processor.TryClaimNextDeliveryAsync(
            NhEmailNotificationDispatcher.DispatcherIdValue,
            CancellationToken.None);
        Assert.Equal(withoutExpiry, regularClaim?.DeliveryId);
        await host.Processor.CompleteDeliveryAttemptAsync(
            NhEmailNotificationDispatcher.DispatcherIdValue,
            regularClaim!,
            TaskResult.Succeeded(),
            CancellationToken.None);

        deliveries = await host.GetDeliveriesAsync();
        Assert.Equal(NotificationDeliveryStatus.Succeeded, deliveries[withoutExpiry].Status);
        Assert.False(deliveries[withoutExpiry].IsCleaned, host.Label);
        Assert.NotNull(deliveries[withoutExpiry].Data);

        // The cleanup pass cancels waiting deliveries whose content expired, also when no worker claims them.
        await host.Processor.CancelExpiredWaitingDeliveriesAsync(CancellationToken.None);

        deliveries = await host.GetDeliveriesAsync();
        Assert.Equal(NotificationDeliveryStatus.Cancelled, deliveries[expiredWhileWaiting].Status);
        AssertDiscarded(deliveries[expiredWhileWaiting], host.Label);
        Assert.Equal(NotificationDeliveryStatus.Queued, deliveries[stillValidWhileWaiting].Status);
        Assert.False(deliveries[stillValidWhileWaiting].IsCleaned, host.Label);
        Assert.NotNull(deliveries[stillValidWhileWaiting].Data);
    }

    private static void AssertDiscarded(NhNotificationDelivery delivery, string label)
    {
        Assert.True(delivery.IsCleaned, label);
        Assert.Null(delivery.Data);
    }

    /// <summary>
    /// The processor and a database of its own, mapped like an application maps the notification tables.
    /// </summary>
    private sealed class DeliveryHost : IAsyncDisposable
    {
        private readonly ServiceProvider _services;
        private readonly Guid _notificationId = Guid.NewGuid();

        private DeliveryHost(ServiceProvider services, string label, NhNotificationProcessingService processor)
        {
            _services = services;
            Label = label;
            Processor = processor;
        }

        internal string Label { get; }

        internal NhNotificationProcessingService Processor { get; }

        internal static async Task<DeliveryHost> CreateAsync(
            RelationalTestProvider provider,
            Action<NhNotificationSettings>? configure = null)
        {
            var database = await ProviderTestDatabase.CreateAsync(provider);
            var services = new ServiceCollection();
            services.AddDbContext<DeliveryDbContext>(options =>
            {
                database.UseNewHeapProvider(options);
                options.ConfigureWarnings(warnings => warnings.Throw(
                    CoreEventId.RowLimitingOperationWithoutOrderByWarning));
            });
            services.AddScoped<IRepository<NhNotificationDelivery>>(serviceProvider =>
                new Repository<NhNotificationDelivery>(
                    serviceProvider.GetRequiredService<DeliveryDbContext>(),
                    serviceProvider));
            services.AddSingleton<INhNotificationDispatcher, TestEmailDispatcher>();
            var serviceProvider = services.BuildServiceProvider();

            await using (var scope = serviceProvider.CreateAsyncScope())
            {
                await scope.ServiceProvider.GetRequiredService<DeliveryDbContext>().Database.EnsureCreatedAsync();
            }

            var settings = new NhNotificationSettings
            {
                ProcessingRetentionPeriod = TimeSpan.FromDays(30),
                ProcessingLockTimeout = TimeSpan.FromMinutes(1),
                ProcessingPollerLockTimeout = TimeSpan.FromSeconds(5),
            };
            configure?.Invoke(settings);
            var processor = new NhNotificationProcessingService(
                serviceProvider.GetRequiredService<IServiceScopeFactory>(),
                NullLogger<NhNotificationProcessingService>.Instance,
                new FixedOptionsMonitor<NhNotificationSettings>(settings),
                TimeSpan.FromHours(1),
                TimeSpan.FromMilliseconds(10));

            var host = new DeliveryHost(serviceProvider, database.Label, processor);
            await host.AddNotificationAsync();
            return host;
        }

        internal async Task<Guid> AddDeliveryAsync(
            NotificationDeliveryStatus status,
            DateTimeOffset scheduledAt,
            DateTimeOffset? lastSendAttemptAt = null,
            DateTimeOffset? sentAt = null,
            DateTimeOffset? lastModifiedAt = null,
            DateTimeOffset? expiresAt = null)
        {
            await using var scope = _services.CreateAsyncScope();
            var context = scope.ServiceProvider.GetRequiredService<DeliveryDbContext>();
            var delivery = new NhNotificationDelivery
            {
                NotificationId = _notificationId,
                DispatcherId = NhEmailNotificationDispatcher.DispatcherIdValue,
                Status = status,
                CreationDateTime = scheduledAt,
                LastModifiedDateTime = lastModifiedAt ?? lastSendAttemptAt ?? scheduledAt,
                ScheduledAt = scheduledAt,
                LastSendAttemptAt = lastSendAttemptAt,
                SentAt = sentAt,
                AttemptCount = lastSendAttemptAt == null ? 0 : 1,
                Data = new NhEmailDeliveryData
                {
                    To = ["recipient@example.test"],
                    Subject = $"{Label} {status}",
                    Body = $"Delivery for {Label} with status {status}.",
                    ExpiresAt = expiresAt,
                },
            };
            context.Set<NhNotificationDelivery>().Add(delivery);
            await context.SaveChangesAsync();

            return delivery.Id;
        }

        internal async Task<Dictionary<Guid, NhNotificationDelivery>> GetDeliveriesAsync()
        {
            await using var scope = _services.CreateAsyncScope();
            var context = scope.ServiceProvider.GetRequiredService<DeliveryDbContext>();

            return await context.Set<NhNotificationDelivery>()
                .AsNoTracking()
                .ToDictionaryAsync(delivery => delivery.Id);
        }

        public async ValueTask DisposeAsync()
        {
            Processor.Dispose();
            await _services.DisposeAsync();
        }

        private async Task AddNotificationAsync()
        {
            await using var scope = _services.CreateAsyncScope();
            var context = scope.ServiceProvider.GetRequiredService<DeliveryDbContext>();
            context.Set<NhNotification>().Add(new NhNotification
            {
                Id = _notificationId,
                Name = $"{Label} retention",
                ProcessorKey = "default",
                Priority = NhNotificationPriority.Normal,
            });
            await context.SaveChangesAsync();
        }
    }

    /// <summary>
    /// Takes the place of the e-mail dispatcher; the tests report the result of each attempt themselves.
    /// </summary>
    private sealed class TestEmailDispatcher : INhNotificationDispatcher<NhEmailDeliveryData>
    {
        public string DispatcherId => NhEmailNotificationDispatcher.DispatcherIdValue;

        public Task<TaskResult> DispatchAsync(NhEmailDeliveryData? deliveryData, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(TaskResult.Succeeded());
        }
    }

    private sealed class DeliveryDbContext(DbContextOptions<DeliveryDbContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.AddNewHeapNotifications();
        }
    }

    private sealed class FixedOptionsMonitor<T>(T currentValue) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; } = currentValue;

        public T Get(string? name)
        {
            return CurrentValue;
        }

        public IDisposable? OnChange(Action<T, string?> listener)
        {
            return null;
        }
    }
}
