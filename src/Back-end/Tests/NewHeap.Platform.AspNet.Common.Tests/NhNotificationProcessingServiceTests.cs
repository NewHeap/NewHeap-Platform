using System.Collections.Concurrent;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NewHeap.Platform.AspNet.Common.DAL;
using NewHeap.Platform.AspNet.Common.DAL.Entities;
using NewHeap.Platform.AspNet.Common.Services.Notification;
using NewHeap.Platform.Common.Models;
using Newtonsoft.Json;
using NSubstitute;
using Xunit;

namespace NewHeap.Platform.AspNet.Common.Tests;

public sealed class NhNotificationProcessingServiceTests
{
    private const string DispatcherId = "TestDispatcher";

    [Fact]
    public async Task DefaultWorkerClaimsOnlyOneDeliveryBeforeDispatchCompletes()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var probe = new DispatcherProbe
        {
            Handler = async (_, cancellationToken) =>
            {
                await release.Task.WaitAsync(cancellationToken);
                return TaskResult.Succeeded();
            }
        };

        await using var harness = CreateHarness(probe);
        var firstId = await harness.AddDeliveryAsync(scheduledAt: DateTimeOffset.UtcNow.AddMinutes(-2));
        var secondId = await harness.AddDeliveryAsync(scheduledAt: DateTimeOffset.UtcNow.AddMinutes(-1));

        await harness.StartAsync();
        await WaitUntilAsync(() => probe.CallCount == 1);
        await Task.Delay(100);

        probe.CallCount.Should().Be(1);
        var deliveries = await harness.GetDeliveriesAsync();
        deliveries.Single(x => x.Id == firstId).Status.Should().Be(NotificationDeliveryStatus.Processing);
        deliveries.Single(x => x.Id == firstId).AttemptCount.Should().Be(1);
        deliveries.Single(x => x.Id == secondId).Status.Should().Be(NotificationDeliveryStatus.Queued);
        deliveries.Single(x => x.Id == secondId).AttemptCount.Should().Be(0);
        harness.Logger.Entries.Should().Contain(entry =>
            entry.Template.StartsWith("Dispatching notification delivery", StringComparison.Ordinal)
            && Equals(entry.Properties["DeliveryId"], firstId)
            && Equals(entry.Properties["DispatcherId"], DispatcherId)
            && Equals(entry.Properties["AttemptNumber"], 1)
            && Equals(entry.Properties["DataType"], typeof(TestDeliveryData).FullName));

        release.SetResult();
        await WaitUntilAsync(async () =>
            (await harness.GetDeliveriesAsync()).All(x => x.Status == NotificationDeliveryStatus.Succeeded));
    }

    [Fact]
    public async Task ConfiguredConcurrencyStartsMultipleAttemptsForTheSameDispatcher()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var probe = new DispatcherProbe
        {
            Handler = async (_, cancellationToken) =>
            {
                await release.Task.WaitAsync(cancellationToken);
                return TaskResult.Succeeded();
            }
        };
        var settings = CreateSettings();
        settings.ProcessingDispatcherConcurrency[DispatcherId] = 2;

        await using var harness = CreateHarness(probe, settings);
        await harness.AddDeliveryAsync(scheduledAt: DateTimeOffset.UtcNow.AddMinutes(-2));
        await harness.AddDeliveryAsync(scheduledAt: DateTimeOffset.UtcNow.AddMinutes(-1));

        await harness.StartAsync();
        await WaitUntilAsync(() => probe.CallCount == 2);

        var deliveries = await harness.GetDeliveriesAsync();
        deliveries.Should().OnlyContain(x =>
            x.Status == NotificationDeliveryStatus.Processing && x.AttemptCount == 1);

        release.SetResult();
        await WaitUntilAsync(async () =>
            (await harness.GetDeliveriesAsync()).All(x => x.Status == NotificationDeliveryStatus.Succeeded));
    }

    [Fact]
    public async Task StaleProcessingDeliveryIsRecoveredWithoutRestart()
    {
        var settings = CreateSettings();
        settings.ProcessingLockTimeout = TimeSpan.FromMilliseconds(20);
        var probe = new DispatcherProbe();

        await using var harness = CreateHarness(probe, settings);
        var deliveryId = await harness.AddDeliveryAsync(
            status: NotificationDeliveryStatus.Processing,
            attemptCount: 1,
            lastSendAttemptAt: DateTimeOffset.UtcNow.AddMinutes(-1));

        await harness.StartAsync();
        await WaitUntilAsync(async () =>
            (await harness.GetDeliveryAsync(deliveryId))?.Status == NotificationDeliveryStatus.Succeeded);

        var delivery = await harness.GetDeliveryAsync(deliveryId);
        delivery!.AttemptCount.Should().Be(2);
        probe.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task LateResultDoesNotOverwriteANewerAttempt()
    {
        await using var harness = CreateHarness(new DispatcherProbe());
        var deliveryId = await harness.AddDeliveryAsync(
            status: NotificationDeliveryStatus.Processing,
            attemptCount: 2,
            lastSendAttemptAt: DateTimeOffset.UtcNow);

        await harness.Processor.CompleteDeliveryAttemptAsync(
            DispatcherId,
            new NhNotificationProcessingService.NotificationDeliveryClaim(deliveryId, 1),
            TaskResult.Succeeded(),
            CancellationToken.None);

        var delivery = await harness.GetDeliveryAsync(deliveryId);
        delivery!.Status.Should().Be(NotificationDeliveryStatus.Processing);
        delivery.AttemptCount.Should().Be(2);
        delivery.SentAt.Should().BeNull();
        harness.Logger.Entries.Should().Contain(entry =>
            entry.Template.StartsWith("Ignoring stale result", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FailedDeliveriesStopAtTheConfiguredMaximumAttemptCount()
    {
        var settings = CreateSettings();
        settings.ProcessingMaxRetryAttempts = 2;
        var probe = new DispatcherProbe
        {
            Handler = (_, _) => Task.FromResult(TaskResult.Failed("Expected dispatch failure"))
        };

        await using var harness = CreateHarness(probe, settings);
        var deliveryId = await harness.AddDeliveryAsync();

        await harness.StartAsync();
        await WaitUntilAsync(async () =>
            (await harness.GetDeliveryAsync(deliveryId))?.Status == NotificationDeliveryStatus.Failed);

        var delivery = await harness.GetDeliveryAsync(deliveryId);
        delivery!.AttemptCount.Should().Be(2);
        delivery.LastFailedMessage.Should().Contain("Expected dispatch failure");
        probe.CallCount.Should().Be(2);
    }

    [Fact]
    public async Task MissingClaimedDeliveryDoesNotStopTheWorker()
    {
        var probe = new DispatcherProbe();
        await using var harness = CreateHarness(probe);
        var deletedDeliveryId = await harness.AddDeliveryAsync(
            scheduledAt: DateTimeOffset.UtcNow.AddMinutes(-2));
        var processedDeliveryId = await harness.AddDeliveryAsync(
            scheduledAt: DateTimeOffset.UtcNow.AddMinutes(-1));
        harness.LockCoordinator.AfterNextCommitAsync = () =>
            harness.DeleteDeliveryAsync(deletedDeliveryId);

        await harness.StartAsync();
        await WaitUntilAsync(async () =>
            (await harness.GetDeliveryAsync(processedDeliveryId))?.Status == NotificationDeliveryStatus.Succeeded);

        (await harness.GetDeliveryAsync(deletedDeliveryId)).Should().BeNull();
        probe.CallCount.Should().Be(1);
    }

    private static NhNotificationSettings CreateSettings()
    {
        return new NhNotificationSettings
        {
            ProcessingCleanupInterval = TimeSpan.FromHours(1),
            ProcessingLockTimeout = TimeSpan.FromMinutes(1),
            ProcessingMaxRetryAttempts = 3,
            ProcessingPollerLockTimeout = TimeSpan.FromMilliseconds(100)
        };
    }

    private static TestHarness CreateHarness(
        DispatcherProbe probe,
        NhNotificationSettings? settings = null)
    {
        settings ??= CreateSettings();
        var services = new ServiceCollection();
        var databaseName = Guid.NewGuid().ToString();
        var lockCoordinator = new TestLockCoordinator();
        var logger = new ListLogger<NhNotificationProcessingService>();

        services.AddDbContext<NotificationTestDbContext>(options =>
            options.UseInMemoryDatabase(databaseName));
        services.AddSingleton(probe);
        services.AddScoped<INhNotificationDispatcher, TestNotificationDispatcher>();
        services.AddScoped<IRepository<NhNotificationDelivery>>(serviceProvider =>
            CreateRepository(
                serviceProvider.GetRequiredService<NotificationTestDbContext>(),
                lockCoordinator));

        var serviceProvider = services.BuildServiceProvider();
        var processor = new NhNotificationProcessingService(
            serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            logger,
            new StaticOptionsMonitor<NhNotificationSettings>(settings),
            TimeSpan.FromMilliseconds(10),
            TimeSpan.FromMilliseconds(10));

        return new TestHarness(serviceProvider, processor, logger, lockCoordinator);
    }

    private static IRepository<NhNotificationDelivery> CreateRepository(
        NotificationTestDbContext dbContext,
        TestLockCoordinator lockCoordinator)
    {
        var repository = Substitute.For<IRepository<NhNotificationDelivery>>();
        repository.Context.Returns(dbContext);
        repository.GetAll().Returns(_ => dbContext.NotificationDeliveries);
        repository.SaveChangesAsync(Arg.Any<CancellationToken>())
            .Returns(call => dbContext.SaveChangesAsync(call.Arg<CancellationToken>()));
        repository.StartOrGetTransactionScopeAsync(Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<INhDbTransactionScope>(lockCoordinator.CreateScope()));
        repository.TryAcquireTransactionLockAsync(
                Arg.Any<INhDbTransactionScope>(),
                Arg.Any<string>(),
                Arg.Any<int>(),
                Arg.Any<CancellationToken>())
            .Returns(call => ((TestTransactionScope)call.Arg<INhDbTransactionScope>()).AcquireAsync(
                call.Arg<int>(),
                call.Arg<CancellationToken>()));
        return repository;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        await WaitUntilAsync(() => Task.FromResult(condition()));
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!await condition())
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    private sealed class TestHarness : IAsyncDisposable
    {
        private readonly ServiceProvider _serviceProvider;
        private bool _started;

        public TestHarness(
            ServiceProvider serviceProvider,
            NhNotificationProcessingService processor,
            ListLogger<NhNotificationProcessingService> logger,
            TestLockCoordinator lockCoordinator)
        {
            _serviceProvider = serviceProvider;
            Processor = processor;
            Logger = logger;
            LockCoordinator = lockCoordinator;
        }

        public NhNotificationProcessingService Processor { get; }
        public ListLogger<NhNotificationProcessingService> Logger { get; }
        public TestLockCoordinator LockCoordinator { get; }

        public async Task StartAsync()
        {
            await Processor.StartAsync(CancellationToken.None);
            _started = true;
        }

        public async Task<Guid> AddDeliveryAsync(
            NotificationDeliveryStatus status = NotificationDeliveryStatus.Queued,
            int attemptCount = 0,
            DateTimeOffset? scheduledAt = null,
            DateTimeOffset? lastSendAttemptAt = null)
        {
            using var scope = _serviceProvider.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<NotificationTestDbContext>();
            var notification = new NhNotification
            {
                Id = Guid.NewGuid(),
                Name = "Notification processor test",
                ProcessorKey = "default",
                Priority = NhNotificationPriority.Normal
            };
            var delivery = new NhNotificationDelivery
            {
                Id = Guid.NewGuid(),
                Notification = notification,
                NotificationId = notification.Id,
                DispatcherId = DispatcherId,
                Data = new TestDeliveryData("Test payload"),
                Status = status,
                AttemptCount = attemptCount,
                ScheduledAt = scheduledAt ?? DateTimeOffset.UtcNow.AddMinutes(-1),
                LastSendAttemptAt = lastSendAttemptAt
            };
            notification.Deliveries.Add(delivery);
            dbContext.Notifications.Add(notification);
            await dbContext.SaveChangesAsync();
            return delivery.Id;
        }

        public async Task DeleteDeliveryAsync(Guid deliveryId)
        {
            using var scope = _serviceProvider.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<NotificationTestDbContext>();
            var delivery = await dbContext.NotificationDeliveries.FindAsync(deliveryId);
            if (delivery is not null)
            {
                dbContext.NotificationDeliveries.Remove(delivery);
                await dbContext.SaveChangesAsync();
            }
        }

        public async Task<NhNotificationDelivery?> GetDeliveryAsync(Guid deliveryId)
        {
            using var scope = _serviceProvider.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<NotificationTestDbContext>();
            return await dbContext.NotificationDeliveries
                .AsNoTracking()
                .SingleOrDefaultAsync(x => x.Id == deliveryId);
        }

        public async Task<List<NhNotificationDelivery>> GetDeliveriesAsync()
        {
            using var scope = _serviceProvider.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<NotificationTestDbContext>();
            return await dbContext.NotificationDeliveries
                .AsNoTracking()
                .OrderBy(x => x.ScheduledAt)
                .ToListAsync();
        }

        public async ValueTask DisposeAsync()
        {
            if (_started)
            {
                await Processor.StopAsync(CancellationToken.None);
            }

            Processor.Dispose();
            await _serviceProvider.DisposeAsync();
        }
    }

    private sealed class NotificationTestDbContext(DbContextOptions<NotificationTestDbContext> options)
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

    private sealed class DispatcherProbe
    {
        private int _callCount;

        public int CallCount => Volatile.Read(ref _callCount);

        public Func<int, CancellationToken, Task<TaskResult>> Handler { get; init; }
            = (_, _) => Task.FromResult(TaskResult.Succeeded());

        public Task<TaskResult> DispatchAsync(CancellationToken cancellationToken)
        {
            var callCount = Interlocked.Increment(ref _callCount);
            return Handler(callCount, cancellationToken);
        }
    }

    private sealed class TestNotificationDispatcher(DispatcherProbe probe)
        : INhNotificationDispatcher<TestDeliveryData>
    {
        public string DispatcherId => NhNotificationProcessingServiceTests.DispatcherId;

        public Task<TaskResult> DispatchAsync(
            TestDeliveryData? deliveryData,
            CancellationToken cancellationToken = default)
        {
            return probe.DispatchAsync(cancellationToken);
        }
    }

    private sealed record TestDeliveryData(string Name);

    private sealed class TestLockCoordinator
    {
        private readonly SemaphoreSlim _semaphore = new(1, 1);
        private Func<Task>? _afterNextCommitAsync;

        public Func<Task>? AfterNextCommitAsync
        {
            set => _afterNextCommitAsync = value;
        }

        public TestTransactionScope CreateScope()
        {
            return new TestTransactionScope(_semaphore, OnCommitAsync);
        }

        private async Task OnCommitAsync()
        {
            var callback = Interlocked.Exchange(ref _afterNextCommitAsync, null);
            if (callback is not null)
            {
                await callback();
            }
        }
    }

    private sealed class TestTransactionScope(
        SemaphoreSlim semaphore,
        Func<Task> onCommitAsync) : INhDbTransactionScope
    {
        private int _ownsLock;

        public bool IsMyTransaction { get; init; } = true;
        public ITransaction Transaction { get; } = Substitute.For<ITransaction>();

        public async Task<bool> AcquireAsync(int timeoutInMilliseconds, CancellationToken cancellationToken)
        {
            var acquired = await semaphore.WaitAsync(
                Math.Max(0, timeoutInMilliseconds),
                cancellationToken);
            if (acquired)
            {
                Interlocked.Exchange(ref _ownsLock, 1);
            }

            return acquired;
        }

        public async Task CommitAsync(CancellationToken cancellationToken = default)
        {
            ReleaseLock();
            await onCommitAsync();
        }

        public Task RollbackAsync(CancellationToken cancellationToken = default)
        {
            ReleaseLock();
            return Task.CompletedTask;
        }

        public void Dispose()
        {
            ReleaseLock();
        }

        public ValueTask DisposeAsync()
        {
            ReleaseLock();
            return ValueTask.CompletedTask;
        }

        private void ReleaseLock()
        {
            if (Interlocked.Exchange(ref _ownsLock, 0) == 1)
            {
                semaphore.Release();
            }
        }
    }

    private sealed class StaticOptionsMonitor<T>(T currentValue) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; } = currentValue;

        public T Get(string? name)
        {
            return CurrentValue;
        }

        public IDisposable? OnChange(Action<T, string?> listener)
        {
            return NoopDisposable.Instance;
        }
    }

    private sealed class NoopDisposable : IDisposable
    {
        public static NoopDisposable Instance { get; } = new();

        public void Dispose()
        {
        }
    }

    private sealed class ListLogger<T> : ILogger<T>
    {
        private readonly ConcurrentQueue<LogEntry> _entries = new();

        public IReadOnlyCollection<LogEntry> Entries => _entries.ToArray();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull
        {
            return NoopDisposable.Instance;
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return true;
        }

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var properties = state as IEnumerable<KeyValuePair<string, object?>>;
            var propertyDictionary = properties?.ToDictionary(x => x.Key, x => x.Value)
                ?? new Dictionary<string, object?>();
            var template = propertyDictionary.TryGetValue("{OriginalFormat}", out var originalFormat)
                ? originalFormat?.ToString() ?? formatter(state, exception)
                : formatter(state, exception);
            _entries.Enqueue(new LogEntry(logLevel, template, propertyDictionary, exception));
        }
    }

    private sealed record LogEntry(
        LogLevel Level,
        string Template,
        IReadOnlyDictionary<string, object?> Properties,
        Exception? Exception);
}
