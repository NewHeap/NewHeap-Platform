using System.Collections.Concurrent;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NewHeap.Platform.AspNet.Common.DAL;
using NewHeap.Platform.AspNet.Common.DAL.Entities;
using NewHeap.Platform.AspNet.Common.Services.BackgroundOperations;
using NewHeap.Platform.AspNet.Common.Utilities;
using NewHeap.Platform.Common.Models;
using NewHeap.Platform.Common.Utilities;
using NewHeap.Platform.Mapping;
using NewHeap.Platform.Tests.Relational;
using NSubstitute;

namespace NewHeap.Platform.AspNet.Common.Tests;

/// <summary>
/// The background-operation components built with dependency injection, as an application does,
/// on an empty database of their own on one relational provider. Tests enqueue and dispatch
/// operations through the service and the dispatcher, and run an attempt with the runner or open
/// its context directly. Every write of the attempt goes through <see cref="WriteFailures"/>, which
/// a test can arm to make one write fail as a database failure would.
/// </summary>
internal sealed class BackgroundOperationProviderHost : IAsyncDisposable
{
    public static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    private BackgroundOperationProviderHost(ServiceProvider services)
    {
        Services = services;
    }

    public ServiceProvider Services { get; }

    public NhBackgroundOperationsOptions Options => Services.GetRequiredService<NhBackgroundOperationsOptions>();

    public NhBackgroundOperationPersistence Persistence => Services.GetRequiredService<NhBackgroundOperationPersistence>();

    public FailingWriteInterceptor WriteFailures => Services.GetRequiredService<FailingWriteInterceptor>();

    /// <summary>
    /// Builds the host. Heartbeats fall due every <paramref name="heartbeatInterval"/>, ten seconds
    /// unless a test needs them sooner; the operation lock gives up after 250 ms.
    /// </summary>
    public static async Task<BackgroundOperationProviderHost> CreateAsync(
        RelationalTestProvider provider,
        Action<NhBackgroundOperationBuilder> register,
        Action<IServiceCollection> configureServices,
        TimeSpan? heartbeatInterval = null)
    {
        var database = await ProviderTestDatabase.CreateAsync(provider);
        var heartbeat = heartbeatInterval ?? TimeSpan.FromSeconds(10);
        var options = new NhBackgroundOperationsOptions
        {
            LiveUpdatesEnabled = false,
            UserNotificationProjectionEnabled = false,
            HeartbeatInterval = heartbeat,
            StaleAttemptTimeout = heartbeat * 6,
            DefaultLeaseDuration = heartbeat * 3,
            TransactionLockTimeoutMilliseconds = 250
        };
        var registryBuilder = new NhBackgroundOperationBuilder(new ServiceCollection(), options);
        register(registryBuilder);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<FailingWriteInterceptor>();
        services.AddDbContext<BackgroundOperationProviderDbContext>((serviceProvider, builder) =>
        {
            database.UseNewHeapProvider(builder);
            builder.AddInterceptors(serviceProvider.GetRequiredService<FailingWriteInterceptor>());
        });
        services.AddScoped<IRepository<NhBackgroundOperation>>(serviceProvider =>
            new Repository<NhBackgroundOperation>(serviceProvider.GetRequiredService<BackgroundOperationProviderDbContext>(), serviceProvider));
        services.AddScoped<IRepository<NhBackgroundOperationLease>>(serviceProvider =>
            new Repository<NhBackgroundOperationLease>(serviceProvider.GetRequiredService<BackgroundOperationProviderDbContext>(), serviceProvider));
        services.AddScoped<IRepository<NhBackgroundOperationAttempt>>(serviceProvider =>
            new Repository<NhBackgroundOperationAttempt>(serviceProvider.GetRequiredService<BackgroundOperationProviderDbContext>(), serviceProvider));
        services.AddSingleton(options);
        services.AddSingleton(registryBuilder.Build());
        services.AddSingleton<INhHangfireQueueNameResolver, NhHangfireQueueNameResolver>();
        services.AddSingleton<INhBackgroundOperationScheduler, NoOpScheduler>();
        services.AddSingleton<INhBackgroundOperationLiveUpdatePublisher, NoOpLiveUpdates>();
        services.AddSingleton<INhBackgroundOperationNotificationProjector, NoOpNotificationProjector>();
        services.AddSingleton<IMapper>(new Mapper(new MapperConfiguration(configuration =>
            configuration.AddProfile<AutomapperProfileConfiguration>())));
        services.AddSingleton(Substitute.For<INhBackgroundOperationOwnerDirectory>());
        services.AddSingleton<NhBackgroundOperationServedQueues>();
        services.AddSingleton<NhBackgroundOperationFanOutCoordinator>();
        services.AddSingleton<NhBackgroundOperationPersistence>();
        services.AddSingleton<NhBackgroundOperationDispatchService>();
        services.AddScoped<NhBackgroundOperationService>();
        services.AddSingleton(TimeProvider.System);
        configureServices(services);

        var serviceProvider = services.BuildServiceProvider();
        await using (var scope = serviceProvider.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<BackgroundOperationProviderDbContext>().Database.EnsureCreatedAsync();
        }

        return new BackgroundOperationProviderHost(serviceProvider);
    }

    public NhBackgroundOperationRunner CreateRunner(ILogger<NhBackgroundOperationRunner> logger)
    {
        return new NhBackgroundOperationRunner(
            Services,
            Services.GetRequiredService<IServiceScopeFactory>(),
            Persistence,
            Services.GetRequiredService<NhBackgroundOperationRegistry>(),
            Services.GetRequiredService<NhBackgroundOperationFanOutCoordinator>(),
            Options,
            logger);
    }

    /// <summary>
    /// Claims a dispatched operation as the runner does and opens the context its handler receives.
    /// </summary>
    public async Task<(NhBackgroundOperationAttemptClaim Claim, NhBackgroundOperationContext Context)> StartAttemptAsync(
        Guid operationId,
        int dispatchGeneration,
        ILogger logger)
    {
        var claim = await Persistence.TryStartAttemptAsync(operationId, dispatchGeneration, CancellationToken.None);
        claim.Should().NotBeNull();
        var context = new NhBackgroundOperationContext(
            claim!,
            Persistence,
            Options,
            new NhBackgroundOperationLeaseManager(Services.GetRequiredService<IServiceScopeFactory>(), Options, claim!),
            Services.GetRequiredService<NhBackgroundOperationFanOutCoordinator>(),
            logger);
        return (claim!, context);
    }

    public async Task<Guid> CreateUserAsync()
    {
        var userId = Guid.NewGuid();
        await using var scope = Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<BackgroundOperationProviderDbContext>();
        context.Users.Add(new NhUser
        {
            Id = userId,
            UserName = $"owner-{userId:N}@example.test",
            NormalizedUserName = $"OWNER-{userId:N}@EXAMPLE.TEST"
        });
        await context.SaveChangesAsync();
        return userId;
    }

    public async Task<Guid> EnqueueAsync<TRequest>(TRequest request, Guid ownerUserId)
        where TRequest : notnull
    {
        await using var scope = Services.CreateAsyncScope();
        var enqueued = await scope.ServiceProvider.GetRequiredService<NhBackgroundOperationService>().EnqueueAsync(
            request,
            new NhBackgroundOperationEnqueueOptions { OwnerUserId = ownerUserId });
        enqueued.Success.Should().BeTrue();
        return enqueued.Data!.Id;
    }

    /// <summary>
    /// Dispatches the operation, as the dispatcher's timer would, and returns its dispatch
    /// generation for the runner.
    /// </summary>
    public async Task<int> DispatchAsync(Guid operationId)
    {
        await Services.GetRequiredService<NhBackgroundOperationDispatchService>().DispatchAvailableAsync(CancellationToken.None);
        var queued = await ReadOperationAsync(operationId);
        queued.Status.Should().Be(NhBackgroundOperationStatus.Queued);
        return queued.DispatchGeneration;
    }

    /// <summary>
    /// Requests the cancellation of a running operation as its owner does. Only the durable state
    /// changes; the attempt hears about it from its heartbeat or its own check.
    /// </summary>
    public async Task RequestCancellationAsync(Guid operationId, Guid ownerUserId)
    {
        await using var scope = Services.CreateAsyncScope();
        var requested = await scope.ServiceProvider.GetRequiredService<NhBackgroundOperationService>()
            .RequestCancellationAsync(operationId, ownerUserId);
        requested.Success.Should().BeTrue();
        requested.Data!.Status.Should().Be(NhBackgroundOperationStatus.CancelRequested);
    }

    /// <summary>
    /// Hands the operation to another attempt of a new dispatch generation, as a recovery does, so
    /// that every later write of the current attempt finds its fence lost.
    /// </summary>
    public async Task MoveToAnotherAttemptAsync(Guid operationId, NhBackgroundOperationStatus status)
    {
        await using var scope = Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<BackgroundOperationProviderDbContext>();
        var operation = await context.BackgroundOperations.SingleAsync(item => item.Id == operationId);
        operation.CurrentAttemptId = Guid.NewGuid();
        operation.DispatchGeneration++;
        operation.Status = status;
        operation.Version++;
        await context.SaveChangesAsync();
    }

    /// <summary>
    /// Holds the operation's transaction lock from another connection, as another node would, until
    /// the returned handle is disposed.
    /// </summary>
    public async Task<IAsyncDisposable> HoldOperationLockAsync(Guid operationId)
    {
        var scope = Services.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<IRepository<NhBackgroundOperation>>();
        var transaction = await repository.StartOrGetTransactionScopeAsync();
        (await repository.TryAcquireTransactionLockAsync(
                transaction,
                $"NhBackgroundOperation:Operation:{operationId:N}",
                10_000))
            .Should().BeTrue();
        return new LockHandle(scope, transaction);
    }

    public async Task<NhBackgroundOperation> ReadOperationAsync(Guid operationId)
    {
        await using var scope = Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<BackgroundOperationProviderDbContext>().BackgroundOperations
            .AsNoTracking()
            .Include(x => x.Steps)
            .Include(x => x.Events)
            .Include(x => x.Attempts)
            .AsSplitQuery()
            .SingleAsync(x => x.Id == operationId);
    }

    public ValueTask DisposeAsync()
    {
        return Services.DisposeAsync();
    }

    private sealed class LockHandle(AsyncServiceScope scope, INhDbTransactionScope transaction) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await transaction.CommitAsync();
            await transaction.DisposeAsync();
            await scope.DisposeAsync();
        }
    }

    private sealed class NoOpLiveUpdates : INhBackgroundOperationLiveUpdatePublisher
    {
        public Task PublishChangedAsync(
            Guid ownerUserId,
            NhBackgroundOperationChangedMessage message,
            CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }

    private sealed class NoOpNotificationProjector : INhBackgroundOperationNotificationProjector
    {
        public Task<TaskResult> ProjectAsync(Guid operationId, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(TaskResult.Succeeded());
        }
    }

    private sealed class NoOpScheduler : INhBackgroundOperationScheduler
    {
        public Task<NhBackgroundOperationScheduleResult> EnqueueAsync(
            Guid operationId,
            int dispatchGeneration,
            string queue,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new NhBackgroundOperationScheduleResult($"job-{operationId:N}-{dispatchGeneration}"));
        }

        public Task<bool> DeleteAsync(string schedulerJobId, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(true);
        }

        public Task<NhBackgroundOperationExecutionState?> GetStateAsync(
            string schedulerJobId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<NhBackgroundOperationExecutionState?>(null);
        }
    }
}

/// <summary>
/// Fails the save that completes the step with the armed step key, inside the write's transaction,
/// as a database failure would. It fails once and then lets writes through again.
/// </summary>
internal sealed class FailingWriteInterceptor : SaveChangesInterceptor
{
    private string? _stepKey;

    public void FailCompletionOfStep(string stepKey)
    {
        Volatile.Write(ref _stepKey, stepKey);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        var stepKey = Volatile.Read(ref _stepKey);
        if (stepKey is null || eventData.Context is not { } context)
        {
            return ValueTask.FromResult(result);
        }

        var completesStep = context.ChangeTracker.Entries<NhBackgroundOperationEvent>()
            .Any(entry => entry.State == EntityState.Added
                          && entry.Entity.EventType == NhBackgroundOperationEventType.StepCompleted
                          && entry.Entity.StepKey == stepKey);
        if (completesStep && Interlocked.CompareExchange(ref _stepKey, null, stepKey) == stepKey)
        {
            throw new DbUpdateException("The database refused the write of the completed step.");
        }

        return ValueTask.FromResult(result);
    }
}

/// <summary>
/// Keeps every log entry with its level, message and exception.
/// </summary>
internal sealed class CapturingLogger<T> : ILogger<T>
{
    private readonly ConcurrentQueue<CapturedLogEntry> _entries = new();

    public IReadOnlyList<CapturedLogEntry> Entries => _entries.ToArray();

    public IReadOnlyList<CapturedLogEntry> At(LogLevel level)
    {
        return _entries.Where(entry => entry.Level == level).ToArray();
    }

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull
    {
        return null;
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
        _entries.Enqueue(new CapturedLogEntry(logLevel, formatter(state, exception), exception));
    }
}

internal sealed record CapturedLogEntry(LogLevel Level, string Message, Exception? Exception);

internal sealed class BackgroundOperationProviderDbContext(DbContextOptions<BackgroundOperationProviderDbContext> options)
    : NhIdentityDbContext(options);
