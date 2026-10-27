using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NewHeap.Platform.AspNet.Common.DAL;
using NewHeap.Platform.AspNet.Common.DAL.Entities;
using NewHeap.Platform.AspNet.Common.Services.Notification;
using NewHeap.Platform.Tests.Relational;
using Xunit;

namespace NewHeap.Platform.AspNet.Common.Tests;

/// <summary>
/// How long the notification processor keeps the data of a delivery, with the notification model
/// that applications map through <c>AddNewHeapNotifications</c>.
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

        internal static async Task<DeliveryHost> CreateAsync(RelationalTestProvider provider)
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
            DateTimeOffset? lastModifiedAt = null)
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
