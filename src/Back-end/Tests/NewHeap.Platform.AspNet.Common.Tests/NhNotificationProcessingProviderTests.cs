using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NewHeap.Platform.AspNet.Common.DAL;
using NewHeap.Platform.AspNet.Common.DAL.Entities;
using NewHeap.Platform.AspNet.Common.Services.Notification;
using NewHeap.Platform.Common.Models;
using Newtonsoft.Json;
using Testcontainers.MsSql;
using Testcontainers.PostgreSql;
using Xunit;

namespace NewHeap.Platform.AspNet.Common.Tests;

public sealed class NhNotificationProcessingProviderTests
{
    [Fact]
    public async Task ClaimCompletionAndTransactionLocksWorkOnBothRelationalProviders()
    {
        await VerifySqlServerAsync();
        await VerifyPostgreSqlAsync();
    }

    private static async Task VerifySqlServerAsync()
    {
        await using var container = new MsSqlBuilder(
            "mcr.microsoft.com/mssql/server:2022-CU14-ubuntu-22.04").Build();
        await container.StartAsync();
        await VerifyProviderAsync(
            options => options.UseSqlServer(container.GetConnectionString()),
            "sql-server");
    }

    private static async Task VerifyPostgreSqlAsync()
    {
        await using var container = new PostgreSqlBuilder("postgres:15.1").Build();
        await container.StartAsync();
        await VerifyProviderAsync(
            options => options.UseNpgsql(container.GetConnectionString()),
            "postgresql");
    }

    private static async Task VerifyProviderAsync(
        Action<DbContextOptionsBuilder> configureProvider,
        string providerName)
    {
        var services = new ServiceCollection();
        services.AddDbContext<ProviderNotificationDbContext>(configureProvider);
        services.AddScoped<IRepository<NhNotificationDelivery>>(serviceProvider =>
            new Repository<NhNotificationDelivery>(
                serviceProvider.GetRequiredService<ProviderNotificationDbContext>(),
                serviceProvider));
        await using var serviceProvider = services.BuildServiceProvider();

        var deliveryId = await SeedDeliveryAsync(serviceProvider, providerName);
        var settings = new NhNotificationSettings
        {
            ProcessingLockTimeout = TimeSpan.FromSeconds(1),
            ProcessingPollerLockTimeout = TimeSpan.FromMilliseconds(100)
        };
        using var processor = new NhNotificationProcessingService(
            serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<NhNotificationProcessingService>.Instance,
            new FixedOptionsMonitor<NhNotificationSettings>(settings),
            TimeSpan.FromMilliseconds(10),
            TimeSpan.FromMilliseconds(10));

        var claim = await processor.TryClaimNextDeliveryAsync("ProviderTestDispatcher", CancellationToken.None);
        claim.Should().NotBeNull();
        claim!.DeliveryId.Should().Be(deliveryId);
        claim.AttemptNumber.Should().Be(1);

        await processor.CompleteDeliveryAttemptAsync(
            "ProviderTestDispatcher",
            claim,
            TaskResult.Succeeded(),
            CancellationToken.None);

        using (var verificationScope = serviceProvider.CreateScope())
        {
            var dbContext = verificationScope.ServiceProvider.GetRequiredService<ProviderNotificationDbContext>();
            var delivery = await dbContext.NotificationDeliveries
                .AsNoTracking()
                .SingleAsync(x => x.Id == deliveryId);
            delivery.Status.Should().Be(NotificationDeliveryStatus.Succeeded);
            delivery.AttemptCount.Should().Be(1);
        }

        using var firstScope = serviceProvider.CreateScope();
        using var secondScope = serviceProvider.CreateScope();
        var firstRepository = firstScope.ServiceProvider.GetRequiredService<IRepository<NhNotificationDelivery>>();
        var secondRepository = secondScope.ServiceProvider.GetRequiredService<IRepository<NhNotificationDelivery>>();
        await using var firstTransaction = await firstRepository.StartOrGetTransactionScopeAsync();
        await using var secondTransaction = await secondRepository.StartOrGetTransactionScopeAsync();

        (await firstRepository.TryAcquireTransactionLockAsync(
            firstTransaction,
            "NotificationProviderIntegrationTest",
            100)).Should().BeTrue();
        (await secondRepository.TryAcquireTransactionLockAsync(
            secondTransaction,
            "NotificationProviderIntegrationTest",
            50)).Should().BeFalse();

        await firstTransaction.CommitAsync();

        (await secondRepository.TryAcquireTransactionLockAsync(
            secondTransaction,
            "NotificationProviderIntegrationTest",
            500)).Should().BeTrue();
        await secondTransaction.CommitAsync();
    }

    private static async Task<Guid> SeedDeliveryAsync(
        ServiceProvider serviceProvider,
        string providerName)
    {
        using var scope = serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ProviderNotificationDbContext>();
        await dbContext.Database.EnsureCreatedAsync();

        var notification = new NhNotification
        {
            Id = Guid.NewGuid(),
            Name = $"{providerName} notification",
            ProcessorKey = "default",
            Priority = NhNotificationPriority.High
        };
        var delivery = new NhNotificationDelivery
        {
            Id = Guid.NewGuid(),
            Notification = notification,
            NotificationId = notification.Id,
            DispatcherId = "ProviderTestDispatcher",
            Data = new { Provider = providerName },
            ScheduledAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            Status = NotificationDeliveryStatus.Queued
        };
        notification.Deliveries.Add(delivery);
        dbContext.Notifications.Add(notification);
        await dbContext.SaveChangesAsync();
        return delivery.Id;
    }

    private sealed class ProviderNotificationDbContext(DbContextOptions<ProviderNotificationDbContext> options)
        : DbContext(options)
    {
        public DbSet<NhNotification> Notifications => Set<NhNotification>();
        public DbSet<NhNotificationDelivery> NotificationDeliveries => Set<NhNotificationDelivery>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<NhNotificationDelivery>(entity =>
            {
                entity.HasOne(x => x.Notification)
                    .WithMany(x => x.Deliveries)
                    .HasForeignKey(x => x.NotificationId)
                    .OnDelete(DeleteBehavior.Cascade);
                entity.Property(x => x.Data).HasConversion(
                    value => value == null ? null : JsonConvert.SerializeObject(value),
                    value => string.IsNullOrWhiteSpace(value) ? null : JsonConvert.DeserializeObject(value));
            });
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
            return EmptyDisposable.Instance;
        }
    }

    private sealed class EmptyDisposable : IDisposable
    {
        public static EmptyDisposable Instance { get; } = new();

        public void Dispose()
        {
        }
    }
}
