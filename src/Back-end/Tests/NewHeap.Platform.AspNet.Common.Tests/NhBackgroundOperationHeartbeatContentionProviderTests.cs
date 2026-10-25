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
using Xunit;

namespace NewHeap.Platform.AspNet.Common.Tests;

/// <summary>
/// The heartbeat of a running attempt and the attempt's own writes take the same per-operation
/// transaction lock. A beat that falls due while the attempt's own write holds that lock past the
/// lock timeout is skipped instead of stopping the attempt, which completes or suspends as its
/// handler intends. Another holder of the lock still stops the attempt as contention, which
/// reschedules it without spending a retry. A skipped beat refreshes nothing: an own write that
/// never ends lets the heartbeat go stale, and reconciliation recovers the attempt after
/// <c>StaleAttemptTimeout</c> on the system clock and fences off its late writes. On both
/// relational providers.
/// </summary>
public sealed class NhBackgroundOperationHeartbeatContentionProviderTests
{
    private const string AgentKey = "assistant-agent:heartbeat-contention";
    private const string SlowWriteMessageKey = "heartbeat.slow-write";
    private const string HungWriteMessageKey = "heartbeat.hung-write";
    private const string SignalWaitStartedMessageKey = "background-operation.signal-wait-started";
    private const string SkippedBeatMessage = "Skipped a heartbeat";

    /// <summary>
    /// How long the attempt's own write holds the operation lock: four lock timeouts and ten
    /// heartbeat intervals.
    /// </summary>
    private static readonly TimeSpan SlowWriteDuration = TimeSpan.FromSeconds(1);

    /// <summary>
    /// How long every heartbeat takes when heartbeats are slow: three heartbeat intervals, so the
    /// next beat is due as soon as one ends.
    /// </summary>
    private static readonly TimeSpan SlowHeartbeatDuration = TimeSpan.FromMilliseconds(300);

    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    public enum OwnWriteOutcome
    {
        Completes,
        Suspends
    }

    /// <summary>
    /// The attempt's own write holds the operation lock longer than the lock timeout, as a first EF
    /// query compilation under load may, while heartbeats fall due. The heartbeat skips instead of
    /// waiting for the lock behind that write, and the attempt is not cancelled: a message write
    /// lets the operation complete, and a signal suspension, an own write as well, suspends it.
    /// </summary>
    [Theory]
    [InlineData(RelationalTestProvider.SqlServer, OwnWriteOutcome.Completes)]
    [InlineData(RelationalTestProvider.SqlServer, OwnWriteOutcome.Suspends)]
    [InlineData(RelationalTestProvider.PostgreSql, OwnWriteOutcome.Completes)]
    [InlineData(RelationalTestProvider.PostgreSql, OwnWriteOutcome.Suspends)]
    public async Task A_heartbeat_skips_while_the_attempts_own_write_holds_the_lock_past_the_lock_timeout(
        RelationalTestProvider provider,
        OwnWriteOutcome outcome)
    {
        await using var services = await CreateServicesAsync(provider);
        var options = services.GetRequiredService<NhBackgroundOperationsOptions>();
        SlowWriteDuration.Should().BeGreaterThan(TimeSpan.FromMilliseconds(options.TransactionLockTimeoutMilliseconds * 2));
        var slowWrites = services.GetRequiredService<SlowWriteInterceptor>();
        slowWrites.Hold(
            outcome == OwnWriteOutcome.Completes ? SlowWriteMessageKey : SignalWaitStartedMessageKey,
            SlowWriteDuration);
        var operationId = await EnqueueAsync(
            services,
            new OwnWriteRequest(outcome == OwnWriteOutcome.Suspends, DateTimeOffset.UtcNow.AddHours(1)));
        var generation = await DispatchAsync(services, operationId);
        var logger = new RecordingLogger<NhBackgroundOperationRunner>();

        await CreateRunner(services, logger).RunAsync(operationId, generation).WaitAsync(Patience);

        slowWrites.HeldWrites.Should().Be(1);
        var operation = await ReadOperationAsync(services, operationId);
        var attempt = operation.Attempts.Should().ContainSingle().Subject;
        operation.FailureCode.Should().BeNull();
        attempt.FailureCode.Should().BeNull();
        logger.Messages(LogLevel.Debug).Should().Contain(message => message.StartsWith(SkippedBeatMessage, StringComparison.Ordinal));
        if (outcome == OwnWriteOutcome.Completes)
        {
            operation.Status.Should().Be(NhBackgroundOperationStatus.Succeeded);
            attempt.Status.Should().Be(NhBackgroundOperationAttemptStatus.Succeeded);
            operation.Events.Should().ContainSingle(operationEvent => operationEvent.MessageKey == SlowWriteMessageKey);
        }
        else
        {
            operation.Status.Should().Be(NhBackgroundOperationStatus.WaitingForSignal);
            operation.CurrentAttemptId.Should().BeNull();
            attempt.Status.Should().Be(NhBackgroundOperationAttemptStatus.Suspended);
            operation.Events.Should().ContainSingle(operationEvent => operationEvent.MessageKey == SignalWaitStartedMessageKey);
        }
    }

    /// <summary>
    /// Every heartbeat takes longer than the heartbeat interval, as on a busy database, so the next
    /// beat is due the moment one ends. The attempt's own write, which starts while a beat runs,
    /// waits for that beat only and goes first after it, and so does the completion: the operation
    /// completes or suspends while its heartbeats go on.
    /// </summary>
    [Theory]
    [InlineData(RelationalTestProvider.SqlServer, OwnWriteOutcome.Completes)]
    [InlineData(RelationalTestProvider.SqlServer, OwnWriteOutcome.Suspends)]
    [InlineData(RelationalTestProvider.PostgreSql, OwnWriteOutcome.Completes)]
    [InlineData(RelationalTestProvider.PostgreSql, OwnWriteOutcome.Suspends)]
    public async Task Own_writes_go_first_after_a_beat_while_every_heartbeat_takes_longer_than_the_interval(
        RelationalTestProvider provider,
        OwnWriteOutcome outcome)
    {
        await using var services = await CreateServicesAsync(provider);
        var options = services.GetRequiredService<NhBackgroundOperationsOptions>();
        SlowHeartbeatDuration.Should().BeGreaterThan(options.HeartbeatInterval * 2);
        var slowWrites = services.GetRequiredService<SlowWriteInterceptor>();
        slowWrites.HoldHeartbeats(SlowHeartbeatDuration);
        var operationId = await EnqueueAsync(
            services,
            new WriteDuringBeatRequest(outcome == OwnWriteOutcome.Suspends, DateTimeOffset.UtcNow.AddHours(1)));
        var generation = await DispatchAsync(services, operationId);

        await CreateRunner(services, new RecordingLogger<NhBackgroundOperationRunner>())
            .RunAsync(operationId, generation)
            .WaitAsync(Patience);

        slowWrites.HeldHeartbeats.Should().BeGreaterThan(0);
        var operation = await ReadOperationAsync(services, operationId);
        operation.FailureCode.Should().BeNull();
        var attempt = operation.Attempts.Should().ContainSingle().Subject;
        if (outcome == OwnWriteOutcome.Completes)
        {
            operation.Status.Should().Be(NhBackgroundOperationStatus.Succeeded);
            attempt.Status.Should().Be(NhBackgroundOperationAttemptStatus.Succeeded);
            operation.Events.Should().ContainSingle(operationEvent => operationEvent.MessageKey == SlowWriteMessageKey);
        }
        else
        {
            operation.Status.Should().Be(NhBackgroundOperationStatus.WaitingForSignal);
            attempt.Status.Should().Be(NhBackgroundOperationAttemptStatus.Suspended);
            operation.Events.Should().ContainSingle(operationEvent => operationEvent.MessageKey == SignalWaitStartedMessageKey);
        }
    }

    /// <summary>
    /// Another connection, such as another node, holds the operation's lock past the lock timeout
    /// while the attempt makes no write of its own. The heartbeat stops the attempt as contention,
    /// as before: the operation is rescheduled as busy and the abandoned attempt spends no retry.
    /// </summary>
    [Theory]
    [InlineData(RelationalTestProvider.SqlServer)]
    [InlineData(RelationalTestProvider.PostgreSql)]
    public async Task Another_holder_of_the_lock_past_the_lock_timeout_still_reschedules_the_attempt_as_contention(
        RelationalTestProvider provider)
    {
        await using var services = await CreateServicesAsync(provider);
        var state = services.GetRequiredService<WaitingHandlerState>();
        var operationId = await EnqueueAsync(services, new WaitingRequest());
        var generation = await DispatchAsync(services, operationId);
        var logger = new RecordingLogger<NhBackgroundOperationRunner>();
        var run = CreateRunner(services, logger).RunAsync(operationId, generation);
        await state.Started.Task.WaitAsync(Patience);

        await using (var holderScope = services.CreateAsyncScope())
        {
            var repository = holderScope.ServiceProvider.GetRequiredService<IRepository<NhBackgroundOperation>>();
            await using var transaction = await repository.StartOrGetTransactionScopeAsync();
            (await repository.TryAcquireTransactionLockAsync(
                    transaction,
                    $"NhBackgroundOperation:Operation:{operationId:N}",
                    10_000))
                .Should().BeTrue();

            await state.Cancelled.Task.WaitAsync(Patience);
            await transaction.CommitAsync();
        }

        state.LockReleased.TrySetResult();
        await run.WaitAsync(Patience);

        var operation = await ReadOperationAsync(services, operationId);
        operation.Status.Should().Be(NhBackgroundOperationStatus.RetryScheduled);
        operation.CurrentAttemptId.Should().BeNull();
        operation.FailureCode.Should().Be("concurrency-busy");
        operation.FailureMessageKey.Should().Be("background-operation.waiting-for-resource");
        operation.NextDispatchAt.Should().NotBeNull();
        operation.Attempts.Should().ContainSingle()
            .Which.Status.Should().Be(NhBackgroundOperationAttemptStatus.Abandoned);
        (await services.GetRequiredService<NhBackgroundOperationPersistence>()
                .GetFailedAttemptCountAsync(operationId, CancellationToken.None))
            .Should().Be(0);
        logger.Messages(LogLevel.Debug).Should().NotContain(message => message.StartsWith(SkippedBeatMessage, StringComparison.Ordinal));
    }

    /// <summary>
    /// The attempt's own write never ends: here the live update of a committed message hangs, so
    /// the write holds no lock. Skipped and waiting beats refresh nothing, so the heartbeat goes
    /// stale on the system clock, and reconciliation recovers the attempt once it is older than
    /// <c>StaleAttemptTimeout</c>. When the write ends after all, the late attempt finds itself
    /// fenced off and leaves the recovered operation as it is.
    /// </summary>
    [Theory]
    [InlineData(RelationalTestProvider.SqlServer)]
    [InlineData(RelationalTestProvider.PostgreSql)]
    public async Task An_own_write_that_never_ends_lets_reconciliation_recover_the_attempt_after_the_stale_timeout(
        RelationalTestProvider provider)
    {
        await using var services = await CreateServicesAsync(provider);
        var options = services.GetRequiredService<NhBackgroundOperationsOptions>();
        var liveUpdates = services.GetRequiredService<HangingLiveUpdates>();
        // A person's operation, whose changes reach its owner live.
        var operationId = await EnqueueAsync(services, new HungWriteRequest(), ownerUserId: await CreateUserAsync(services));
        var generation = await DispatchAsync(services, operationId);
        var logger = new RecordingLogger<NhBackgroundOperationRunner>();
        var run = CreateRunner(services, logger).RunAsync(operationId, generation);
        NhBackgroundOperation recovered;
        Guid attemptId;
        try
        {
            await liveUpdates.Hung.Task.WaitAsync(Patience);
            var hung = await ReadOperationAsync(services, operationId);
            hung.Status.Should().Be(NhBackgroundOperationStatus.Running);
            hung.HeartbeatAt.Should().NotBeNull();
            attemptId = hung.CurrentAttemptId!.Value;

            recovered = await ReconcileUntilRecoveredAsync(
                services,
                operationId,
                attemptId,
                options.StaleAttemptTimeout + Patience);

            // No beat refreshed the heartbeat while the write hung.
            recovered.HeartbeatAt.Should().Be(hung.HeartbeatAt);
            recovered.Attempts.Single(item => item.Id == attemptId).HeartbeatAt
                .Should().Be(hung.Attempts.Single(item => item.Id == attemptId).HeartbeatAt);
            logger.Messages(LogLevel.Debug).Should().Contain(message => message.StartsWith(SkippedBeatMessage, StringComparison.Ordinal));
        }
        finally
        {
            liveUpdates.Release();
        }

        recovered.Status.Should().Be(NhBackgroundOperationStatus.RetryScheduled);
        recovered.CurrentAttemptId.Should().BeNull();
        recovered.DispatchGeneration.Should().Be(generation + 1);
        var abandoned = recovered.Attempts.Single(item => item.Id == attemptId);
        abandoned.Status.Should().Be(NhBackgroundOperationAttemptStatus.Abandoned);
        abandoned.RecoveryReason.Should().Be("stale-heartbeat");
        recovered.Events.Should().ContainSingle(operationEvent => operationEvent.MessageKey == "background-operation.stale-attempt-recovered");

        await run.WaitAsync(Patience);

        var after = await ReadOperationAsync(services, operationId);
        after.Status.Should().Be(NhBackgroundOperationStatus.RetryScheduled);
        after.Version.Should().Be(recovered.Version);
        after.Events.Should().HaveCount(recovered.Events.Count);
        after.Attempts.Single(item => item.Id == attemptId).Status.Should().Be(NhBackgroundOperationAttemptStatus.Abandoned);
    }

    /// <summary>
    /// Runs reconciliation until it recovered the attempt, as its timer would. Recovery waits until
    /// the attempt's heartbeat is older than <c>StaleAttemptTimeout</c> on the system clock.
    /// </summary>
    private static async Task<NhBackgroundOperation> ReconcileUntilRecoveredAsync(
        IServiceProvider services,
        Guid operationId,
        Guid attemptId,
        TimeSpan patience)
    {
        var reconciler = services.GetRequiredService<NhBackgroundOperationReconciliationService>();
        var deadline = DateTimeOffset.UtcNow + patience;
        while (true)
        {
            await reconciler.ReconcileAsync(CancellationToken.None);
            var operation = await ReadOperationAsync(services, operationId);
            if (operation.CurrentAttemptId != attemptId)
            {
                return operation;
            }

            DateTimeOffset.UtcNow.Should().BeBefore(deadline, "reconciliation recovers an attempt whose heartbeat is stale");
            await Task.Delay(TimeSpan.FromMilliseconds(100));
        }
    }

    private static NhBackgroundOperationRunner CreateRunner(
        ServiceProvider services,
        ILogger<NhBackgroundOperationRunner> logger)
    {
        return new NhBackgroundOperationRunner(
            services,
            services.GetRequiredService<IServiceScopeFactory>(),
            services.GetRequiredService<NhBackgroundOperationPersistence>(),
            services.GetRequiredService<NhBackgroundOperationRegistry>(),
            services.GetRequiredService<NhBackgroundOperationFanOutCoordinator>(),
            services.GetRequiredService<NhBackgroundOperationsOptions>(),
            logger);
    }

    /// <summary>
    /// Enqueues an operation of an agent, or of the person <paramref name="ownerUserId"/>.
    /// </summary>
    private static async Task<Guid> EnqueueAsync<TRequest>(IServiceProvider services, TRequest request, Guid? ownerUserId = null)
        where TRequest : notnull
    {
        await using var scope = services.CreateAsyncScope();
        var enqueued = await scope.ServiceProvider.GetRequiredService<NhBackgroundOperationService>().EnqueueAsync(
            request,
            ownerUserId is { } userId
                ? new NhBackgroundOperationEnqueueOptions { OwnerUserId = userId }
                : new NhBackgroundOperationEnqueueOptions { OwnerAgentKey = AgentKey });
        enqueued.Success.Should().BeTrue();
        return enqueued.Data!.Id;
    }

    /// <summary>
    /// Dispatches the operation, as the dispatcher's timer would, and returns its dispatch
    /// generation for the runner.
    /// </summary>
    private static async Task<int> DispatchAsync(IServiceProvider services, Guid operationId)
    {
        await services.GetRequiredService<NhBackgroundOperationDispatchService>().DispatchAvailableAsync(CancellationToken.None);
        var queued = await ReadOperationAsync(services, operationId);
        queued.Status.Should().Be(NhBackgroundOperationStatus.Queued);
        return queued.DispatchGeneration;
    }

    private static async Task<Guid> CreateUserAsync(IServiceProvider services)
    {
        var userId = Guid.NewGuid();
        await using var scope = services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<HeartbeatContentionDbContext>();
        context.Users.Add(new NhUser
        {
            Id = userId,
            UserName = $"owner-{userId:N}@example.test",
            NormalizedUserName = $"OWNER-{userId:N}@EXAMPLE.TEST"
        });
        await context.SaveChangesAsync();
        return userId;
    }

    private static async Task<NhBackgroundOperation> ReadOperationAsync(IServiceProvider services, Guid operationId)
    {
        await using var scope = services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<HeartbeatContentionDbContext>().BackgroundOperations
            .AsNoTracking()
            .Include(x => x.Steps)
            .Include(x => x.Events)
            .Include(x => x.Attempts)
            .AsSplitQuery()
            .SingleAsync(x => x.Id == operationId);
    }

    /// <summary>
    /// Builds the background-operation components with dependency injection, as an application
    /// does, and creates the database. Heartbeats fall due every 100 ms and give up on the operation
    /// lock after 250 ms; an attempt is stale after two seconds without a heartbeat.
    /// </summary>
    private static async Task<ServiceProvider> CreateServicesAsync(RelationalTestProvider provider)
    {
        var database = await ProviderTestDatabase.CreateAsync(provider);
        var options = new NhBackgroundOperationsOptions
        {
            LiveUpdatesEnabled = false,
            HeartbeatInterval = TimeSpan.FromMilliseconds(100),
            StaleAttemptTimeout = TimeSpan.FromSeconds(2),
            DefaultLeaseDuration = TimeSpan.FromSeconds(2),
            TransactionLockTimeoutMilliseconds = 250
        };
        var registryBuilder = new NhBackgroundOperationBuilder(new ServiceCollection(), options);
        registryBuilder.Add<OwnWriteRequest, OwnWriteHandler>("heartbeat-own-write");
        registryBuilder.Add<WaitingRequest, WaitingHandler>("heartbeat-waiting");
        registryBuilder.Add<HungWriteRequest, HungWriteHandler>("heartbeat-hung-write");
        registryBuilder.Add<WriteDuringBeatRequest, WriteDuringBeatHandler>("heartbeat-write-during-beat");
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<SlowWriteInterceptor>();
        services.AddDbContext<HeartbeatContentionDbContext>((serviceProvider, builder) =>
        {
            database.UseNewHeapProvider(builder);
            builder.AddInterceptors(serviceProvider.GetRequiredService<SlowWriteInterceptor>());
        });
        services.AddScoped<IRepository<NhBackgroundOperation>>(serviceProvider =>
            new Repository<NhBackgroundOperation>(serviceProvider.GetRequiredService<HeartbeatContentionDbContext>(), serviceProvider));
        services.AddScoped<IRepository<NhBackgroundOperationLease>>(serviceProvider =>
            new Repository<NhBackgroundOperationLease>(serviceProvider.GetRequiredService<HeartbeatContentionDbContext>(), serviceProvider));
        services.AddScoped<IRepository<NhBackgroundOperationAttempt>>(serviceProvider =>
            new Repository<NhBackgroundOperationAttempt>(serviceProvider.GetRequiredService<HeartbeatContentionDbContext>(), serviceProvider));
        services.AddSingleton(options);
        services.AddSingleton(registryBuilder.Build());
        services.AddSingleton<INhHangfireQueueNameResolver, NhHangfireQueueNameResolver>();
        services.AddSingleton<INhBackgroundOperationScheduler, NoOpScheduler>();
        services.AddSingleton<HangingLiveUpdates>();
        services.AddSingleton<INhBackgroundOperationLiveUpdatePublisher>(serviceProvider => serviceProvider.GetRequiredService<HangingLiveUpdates>());
        services.AddSingleton<INhBackgroundOperationNotificationProjector, NoOpNotificationProjector>();
        services.AddSingleton<IMapper>(new Mapper(new MapperConfiguration(configuration =>
            configuration.AddProfile<AutomapperProfileConfiguration>())));
        services.AddSingleton(Substitute.For<INhBackgroundOperationOwnerDirectory>());
        services.AddSingleton<NhBackgroundOperationServedQueues>();
        services.AddSingleton<NhBackgroundOperationFanOutCoordinator>();
        services.AddSingleton<NhBackgroundOperationPersistence>();
        services.AddSingleton<NhBackgroundOperationDispatchService>();
        services.AddSingleton<NhBackgroundOperationReconciliationService>();
        services.AddScoped<NhBackgroundOperationService>();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<WaitingHandlerState>();
        services.AddTransient<OwnWriteHandler>();
        services.AddTransient<WaitingHandler>();
        services.AddTransient<HungWriteHandler>();
        services.AddTransient<WriteDuringBeatHandler>();
        var serviceProvider = services.BuildServiceProvider();
        await using (var scope = serviceProvider.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<HeartbeatContentionDbContext>().Database.EnsureCreatedAsync();
        }

        return serviceProvider;
    }

    private sealed record OwnWriteRequest(bool Suspend, DateTimeOffset ExpiresAt);

    private sealed record WriteDuringBeatRequest(bool Suspend, DateTimeOffset ExpiresAt);

    private sealed record ApprovalSignal(bool Approved);

    private sealed record WaitingRequest;

    private sealed record HungWriteRequest;

    /// <summary>
    /// Makes one own write: a message, or a signal suspension.
    /// </summary>
    private sealed class OwnWriteHandler : INhBackgroundOperationHandler<OwnWriteRequest>
    {
        public async Task<TaskResult> ExecuteAsync(
            OwnWriteRequest request,
            INhBackgroundOperationContext context,
            CancellationToken cancellationToken)
        {
            if (request.Suspend)
            {
                var wait = await context.Suspension.WaitForSignalAsync<ApprovalSignal>(
                    "heartbeat-approval",
                    request.ExpiresAt,
                    cancellationToken: cancellationToken);
                if (wait.Signal is not { Approved: true })
                {
                    return TaskResult.Failed("approval-missing", "heartbeat.approval-missing");
                }

                return TaskResult.Succeeded();
            }

            await context.Messages.PublishAsync(NhBackgroundOperationMessage.Info(SlowWriteMessageKey), cancellationToken);
            return TaskResult.Succeeded();
        }
    }

    private sealed class WaitingHandlerState
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource LockReleased { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>
    /// Waits without writing until the attempt is stopped. It unwinds only once the other holder
    /// released the lock, so the runner can record the rescheduling.
    /// </summary>
    private sealed class WaitingHandler(WaitingHandlerState state) : INhBackgroundOperationHandler<WaitingRequest>
    {
        public async Task<TaskResult> ExecuteAsync(
            WaitingRequest request,
            INhBackgroundOperationContext context,
            CancellationToken cancellationToken)
        {
            state.Started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                state.Cancelled.TrySetResult();
                await state.LockReleased.Task.WaitAsync(Patience);
                throw;
            }

            return TaskResult.Succeeded();
        }
    }

    /// <summary>
    /// Publishes a message whose live update never returns.
    /// </summary>
    private sealed class HungWriteHandler(HangingLiveUpdates liveUpdates) : INhBackgroundOperationHandler<HungWriteRequest>
    {
        public async Task<TaskResult> ExecuteAsync(
            HungWriteRequest request,
            INhBackgroundOperationContext context,
            CancellationToken cancellationToken)
        {
            liveUpdates.HangNext();
            await context.Messages.PublishAsync(NhBackgroundOperationMessage.Info(HungWriteMessageKey), cancellationToken);
            return TaskResult.Succeeded();
        }
    }

    /// <summary>
    /// Makes one own write, a message or a signal suspension, while a heartbeat runs.
    /// </summary>
    private sealed class WriteDuringBeatHandler(SlowWriteInterceptor slowWrites) : INhBackgroundOperationHandler<WriteDuringBeatRequest>
    {
        public async Task<TaskResult> ExecuteAsync(
            WriteDuringBeatRequest request,
            INhBackgroundOperationContext context,
            CancellationToken cancellationToken)
        {
            await slowWrites.HeartbeatHeld.Task.WaitAsync(Patience, cancellationToken);
            if (request.Suspend)
            {
                var wait = await context.Suspension.WaitForSignalAsync<ApprovalSignal>(
                    "heartbeat-approval",
                    request.ExpiresAt,
                    cancellationToken: cancellationToken);
                return wait.Signal is { Approved: true }
                    ? TaskResult.Succeeded()
                    : TaskResult.Failed("approval-missing", "heartbeat.approval-missing");
            }

            await context.Messages.PublishAsync(NhBackgroundOperationMessage.Info(SlowWriteMessageKey), cancellationToken);
            return TaskResult.Succeeded();
        }
    }

    /// <summary>
    /// Holds a save that adds the event with the configured message key, inside the write's
    /// transaction and so inside its operation lock, or every heartbeat's save, inside the beat's
    /// transaction. The delay ignores cancellation, as the compilation of a first EF query or a busy
    /// database does.
    /// </summary>
    private sealed class SlowWriteInterceptor : SaveChangesInterceptor
    {
        private string? _messageKey;
        private TimeSpan _duration;
        private int _heldWrites;
        private TimeSpan? _heartbeatDuration;
        private int _heldHeartbeats;

        public int HeldWrites => Volatile.Read(ref _heldWrites);

        public int HeldHeartbeats => Volatile.Read(ref _heldHeartbeats);

        /// <summary>
        /// Completes when the first held heartbeat runs.
        /// </summary>
        public TaskCompletionSource HeartbeatHeld { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Hold(string messageKey, TimeSpan duration)
        {
            _duration = duration;
            Volatile.Write(ref _messageKey, messageKey);
        }

        public void HoldHeartbeats(TimeSpan duration)
        {
            _heartbeatDuration = duration;
        }

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (eventData.Context is not { } context)
            {
                return result;
            }

            if (_heartbeatDuration is { } heartbeatDuration && IsHeartbeat(context))
            {
                Interlocked.Increment(ref _heldHeartbeats);
                HeartbeatHeld.TrySetResult();
                await Task.Delay(heartbeatDuration, CancellationToken.None);
                return result;
            }

            var messageKey = Volatile.Read(ref _messageKey);
            if (messageKey is null)
            {
                return result;
            }

            var holds = context.ChangeTracker.Entries<NhBackgroundOperationEvent>()
                .Any(entry => entry.State == EntityState.Added && entry.Entity.MessageKey == messageKey);
            if (holds)
            {
                Interlocked.Increment(ref _heldWrites);
                await Task.Delay(_duration, CancellationToken.None);
            }

            return result;
        }

        /// <summary>
        /// A heartbeat's save: it refreshes a running attempt's heartbeat and changes nothing else of
        /// the attempt, and adds no event.
        /// </summary>
        private static bool IsHeartbeat(DbContext context)
        {
            return context.ChangeTracker.Entries<NhBackgroundOperationAttempt>()
                       .Any(entry => entry.State == EntityState.Modified
                                     && entry.Property(attempt => attempt.HeartbeatAt).IsModified
                                     && !entry.Property(attempt => attempt.Status).IsModified)
                   && !context.ChangeTracker.Entries<NhBackgroundOperationEvent>()
                       .Any(entry => entry.State == EntityState.Added);
        }
    }

    /// <summary>
    /// Live updates that return at once, except the one requested to hang, which returns only when
    /// released.
    /// </summary>
    private sealed class HangingLiveUpdates : INhBackgroundOperationLiveUpdatePublisher
    {
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _hangNext;

        public TaskCompletionSource Hung { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void HangNext()
        {
            Interlocked.Exchange(ref _hangNext, 1);
        }

        public void Release()
        {
            _released.TrySetResult();
        }

        public async Task PublishChangedAsync(
            Guid ownerUserId,
            NhBackgroundOperationChangedMessage message,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref _hangNext, 0) == 0)
            {
                return;
            }

            Hung.TrySetResult();
            await _released.Task.WaitAsync(TimeSpan.FromMinutes(2));
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

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        private readonly ConcurrentQueue<(LogLevel Level, string Message)> _entries = new();

        public IReadOnlyList<string> Messages(LogLevel level)
        {
            return _entries
                .Where(entry => entry.Level == level)
                .Select(entry => entry.Message)
                .ToArray();
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
            _entries.Enqueue((logLevel, formatter(state, exception)));
        }
    }

    private sealed class HeartbeatContentionDbContext(DbContextOptions<HeartbeatContentionDbContext> options)
        : NhIdentityDbContext(options);
}
