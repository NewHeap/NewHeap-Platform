using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
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
/// A cancellation whose worker stopped: the operation was being cancelled while an attempt held it,
/// and that attempt's heartbeat went stale on the system clock. Reconciliation finishes the
/// cancellation as the worker would have: the operation ends cancelled once, with the cancellation
/// milestone, its notification projection and live update, the stale attempt's leases released and
/// its late writes fenced off, and a fan-out parent ends cancelled once its children did. An attempt
/// with a fresh heartbeat is left to its worker. The readiness check counts the same stale attempts
/// until reconciliation recovered them. A running operation whose attempts keep going stale spends a
/// retry with each of them and fails once its retries are spent. On both relational providers.
/// </summary>
public sealed class NhBackgroundOperationStaleCancellationProviderTests
{
    private const string AgentKey = "assistant-agent:stale-cancellation";
    private const string ResourceKey = "stale-cancellation-resource";

    [Theory]
    [InlineData(RelationalTestProvider.SqlServer)]
    [InlineData(RelationalTestProvider.PostgreSql)]
    public async Task A_cancellation_whose_worker_stopped_ends_cancelled_once_and_releases_its_leases(RelationalTestProvider provider)
    {
        await using var services = await CreateServicesAsync(provider);
        var options = services.GetRequiredService<NhBackgroundOperationsOptions>();
        var reconciler = services.GetRequiredService<NhBackgroundOperationReconciliationService>();
        var healthCheck = CreateHealthCheck(services);
        // A person's operation, whose changes reach its owner live.
        var operationId = await EnqueueAsync(services, new StaleSimpleRequest("cancelled"), ownerUserId: await CreateUserAsync(services));
        var claim = await StartAttemptAsync(services, operationId);
        // The worker holds a lease far longer than the stale timeout.
        var lease = await AcquireLeaseAsync(services, claim);
        await RequestCancellationAsync(services, operationId);
        var requested = await ReadOperationAsync(services, operationId);
        requested.Status.Should().Be(NhBackgroundOperationStatus.CancelRequested);
        requested.CurrentAttemptId.Should().Be(claim.AttemptId);

        // The worker stopped before it saw the request: its heartbeat is stale.
        await BackdateHeartbeatAsync(services, operationId, options.StaleAttemptTimeout + TimeSpan.FromMinutes(1));
        var stale = await healthCheck.CheckHealthAsync(new HealthCheckContext());
        stale.Status.Should().Be(HealthStatus.Degraded);
        stale.Data["staleAttempts"].Should().Be(1);
        var projections = services.GetRequiredService<RecordingNotificationProjector>();
        var liveUpdates = services.GetRequiredService<RecordingLiveUpdates>();
        projections.Clear();
        liveUpdates.Clear();

        (await reconciler.ReconcileAsync(CancellationToken.None)).Should().Be(1);

        var cancelled = await ReadOperationAsync(services, operationId);
        cancelled.Status.Should().Be(NhBackgroundOperationStatus.Cancelled);
        cancelled.CurrentAttemptId.Should().BeNull();
        cancelled.DispatchGeneration.Should().Be(requested.DispatchGeneration + 1);
        cancelled.CompletedAt.Should().NotBeNull();
        cancelled.NextDispatchAt.Should().BeNull();
        cancelled.FailureCode.Should().Be("cancelled");
        cancelled.FailureMessageKey.Should().Be("background-operation.cancelled");
        cancelled.Steps.Single(step => step.StepKey == "root").Status.Should().Be(NhBackgroundOperationStepStatus.Cancelled);
        var milestone = cancelled.Events.Should().ContainSingle(operationEvent => operationEvent.MessageKey == "background-operation.cancelled").Subject;
        milestone.IsMilestone.Should().BeTrue();
        milestone.IsOperatorOnly.Should().BeFalse();
        milestone.EventType.Should().Be(NhBackgroundOperationEventType.StateChanged);
        cancelled.Events.Should().ContainSingle(operationEvent => operationEvent.MessageKey == "background-operation.stale-attempt-recovered"
            && operationEvent.IsOperatorOnly
            && !operationEvent.IsMilestone);
        var attempt = cancelled.Attempts.Single(item => item.Id == claim.AttemptId);
        attempt.Status.Should().Be(NhBackgroundOperationAttemptStatus.Abandoned);
        attempt.RecoveryReason.Should().Be("stale-heartbeat");
        projections.Projected.Should().Contain(operationId);
        liveUpdates.Published.Should().Contain(operationId);
        // The lease is free for other work at once, with a newer fencing token.
        var released = await ReadLeaseAsync(services);
        released.OperationId.Should().BeNull();
        released.AttemptId.Should().BeNull();
        var other = await StartAttemptAsync(services, await EnqueueAsync(services, new StaleSimpleRequest("next")));
        var next = await AcquireLeaseAsync(services, other);
        next.FencingToken.Should().BeGreaterThan(lease.FencingToken);
        await next.DisposeAsync();
        (await healthCheck.CheckHealthAsync(new HealthCheckContext())).Status.Should().Be(HealthStatus.Healthy);

        // Exactly once: a further round finds nothing to do and changes nothing.
        (await reconciler.ReconcileAsync(CancellationToken.None)).Should().Be(0);
        var again = await ReadOperationAsync(services, operationId);
        again.Version.Should().Be(cancelled.Version);
        again.Events.Should().HaveCount(cancelled.Events.Count);
    }

    /// <summary>
    /// The worker that stopped wakes up late: every write with its claim fails on the fence, as
    /// does its lease, and the operation stays cancelled.
    /// </summary>
    [Theory]
    [InlineData(RelationalTestProvider.SqlServer)]
    [InlineData(RelationalTestProvider.PostgreSql)]
    public async Task A_late_write_of_the_stopped_worker_fails_on_its_fencing_token(RelationalTestProvider provider)
    {
        await using var services = await CreateServicesAsync(provider);
        var options = services.GetRequiredService<NhBackgroundOperationsOptions>();
        var persistence = services.GetRequiredService<NhBackgroundOperationPersistence>();
        var operationId = await EnqueueAsync(services, new StaleSimpleRequest("late"));
        var claim = await StartAttemptAsync(services, operationId);
        var lease = await AcquireLeaseAsync(services, claim);
        await RequestCancellationAsync(services, operationId);
        await BackdateHeartbeatAsync(services, operationId, options.StaleAttemptTimeout + TimeSpan.FromMinutes(1));
        await services.GetRequiredService<NhBackgroundOperationReconciliationService>().ReconcileAsync(CancellationToken.None);
        var cancelled = await ReadOperationAsync(services, operationId);
        cancelled.Status.Should().Be(NhBackgroundOperationStatus.Cancelled);

        (await persistence.HeartbeatAsync(claim, CancellationToken.None)).Should().Be(NhBackgroundOperationHeartbeatResult.OwnershipLost);
        var checkpoint = () => persistence.SetCheckpointAsync(claim, "late-checkpoint", "value", 1, null, CancellationToken.None);
        (await checkpoint.Should().ThrowAsync<InvalidOperationException>()).WithMessage("*fencing token*");
        var message = () => persistence.PublishMessageAsync(
            claim,
            NhBackgroundOperationMessage.Info("stale.late-message"),
            CancellationToken.None);
        await message.Should().ThrowAsync<InvalidOperationException>();
        await persistence.CompleteAsync(claim, NhBackgroundOperationStatus.Succeeded, null, null, null, null, CancellationToken.None);
        var renew = () => lease.RenewAsync();
        await renew.Should().ThrowAsync<InvalidOperationException>();
        await lease.DisposeAsync();

        var after = await ReadOperationAsync(services, operationId);
        after.Status.Should().Be(NhBackgroundOperationStatus.Cancelled);
        after.Version.Should().Be(cancelled.Version);
        after.Events.Should().HaveCount(cancelled.Events.Count);
        after.Attempts.Single(item => item.Id == claim.AttemptId).Status.Should().Be(NhBackgroundOperationAttemptStatus.Abandoned);
        (await ReadLeaseAsync(services)).OperationId.Should().BeNull();
    }

    /// <summary>
    /// A cancellation whose worker still sends heartbeats belongs to that worker, which sees the
    /// request at its next heartbeat; reconciliation and the readiness check leave it alone.
    /// </summary>
    [Theory]
    [InlineData(RelationalTestProvider.SqlServer)]
    [InlineData(RelationalTestProvider.PostgreSql)]
    public async Task A_cancellation_whose_worker_still_sends_heartbeats_is_left_to_that_worker(RelationalTestProvider provider)
    {
        await using var services = await CreateServicesAsync(provider);
        var persistence = services.GetRequiredService<NhBackgroundOperationPersistence>();
        var operationId = await EnqueueAsync(services, new StaleSimpleRequest("alive"));
        var claim = await StartAttemptAsync(services, operationId);
        await AcquireLeaseAsync(services, claim);
        await RequestCancellationAsync(services, operationId);
        await BackdateHeartbeatAsync(services, operationId, TimeSpan.FromSeconds(5));
        var requested = await ReadOperationAsync(services, operationId);

        (await services.GetRequiredService<NhBackgroundOperationReconciliationService>().ReconcileAsync(CancellationToken.None)).Should().Be(0);
        (await CreateHealthCheck(services).CheckHealthAsync(new HealthCheckContext())).Status.Should().Be(HealthStatus.Healthy);

        var untouched = await ReadOperationAsync(services, operationId);
        untouched.Status.Should().Be(NhBackgroundOperationStatus.CancelRequested);
        untouched.CurrentAttemptId.Should().Be(claim.AttemptId);
        untouched.Version.Should().Be(requested.Version);
        (await ReadLeaseAsync(services)).AttemptId.Should().Be(claim.AttemptId);
        // The worker sees the request and ends the cancellation itself.
        (await persistence.HeartbeatAsync(claim, CancellationToken.None)).Should().Be(NhBackgroundOperationHeartbeatResult.CancellationRequested);
    }

    /// <summary>
    /// A running operation whose worker stops on every attempt, as the worker of a handler that never
    /// returns does once its heartbeat stops: reconciliation retries it, and every attempt it recovers
    /// spends a retry as a failed attempt does, so the operation fails with
    /// <c>stale-attempt-retries-exhausted</c> once its retries are spent instead of being retried
    /// without end.
    /// </summary>
    [Theory]
    [InlineData(RelationalTestProvider.SqlServer)]
    [InlineData(RelationalTestProvider.PostgreSql)]
    public async Task A_running_operation_whose_attempts_keep_going_stale_fails_once_its_retries_are_spent(RelationalTestProvider provider)
    {
        await using var services = await CreateServicesAsync(provider);
        var options = services.GetRequiredService<NhBackgroundOperationsOptions>();
        var persistence = services.GetRequiredService<NhBackgroundOperationPersistence>();
        var reconciler = services.GetRequiredService<NhBackgroundOperationReconciliationService>();
        var retryCount = services.GetRequiredService<NhBackgroundOperationRegistry>().GetForOperationType("stale-simple").RetryCount;
        retryCount.Should().Be(3);
        var operationId = await EnqueueAsync(services, new StaleSimpleRequest("never-returns"));

        // The first attempt fails as a handler can, and spends a retry.
        var failed = await StartAttemptAsync(services, operationId);
        await persistence.CompleteAsync(
            failed,
            NhBackgroundOperationStatus.Failed,
            "handler-failed",
            "background-operation.failed",
            null,
            DateTimeOffset.UtcNow,
            CancellationToken.None);

        // The worker of every later attempt stops; reconciliation retries while retries are left.
        for (var attemptNumber = 2; attemptNumber <= retryCount; attemptNumber++)
        {
            await StartAttemptAsync(services, operationId);
            await BackdateHeartbeatAsync(services, operationId, options.StaleAttemptTimeout + TimeSpan.FromMinutes(1));
            await reconciler.ReconcileAsync(CancellationToken.None);

            (await ReadOperationAsync(services, operationId)).Status.Should().Be(NhBackgroundOperationStatus.RetryScheduled);
            (await persistence.GetFailedAttemptCountAsync(operationId, CancellationToken.None)).Should().Be(attemptNumber);
        }

        var last = await StartAttemptAsync(services, operationId);
        await BackdateHeartbeatAsync(services, operationId, options.StaleAttemptTimeout + TimeSpan.FromMinutes(1));
        await reconciler.ReconcileAsync(CancellationToken.None);

        var exhausted = await ReadOperationAsync(services, operationId);
        exhausted.Status.Should().Be(NhBackgroundOperationStatus.Failed);
        exhausted.FailureCode.Should().Be("stale-attempt-retries-exhausted");
        exhausted.FailureMessageKey.Should().Be("background-operation.failed");
        exhausted.CurrentAttemptId.Should().BeNull();
        exhausted.CompletedAt.Should().NotBeNull();
        exhausted.Attempts.Should().HaveCount(retryCount + 1);
        var abandoned = exhausted.Attempts.Single(item => item.Id == last.AttemptId);
        abandoned.Status.Should().Be(NhBackgroundOperationAttemptStatus.Abandoned);
        abandoned.RecoveryReason.Should().Be("stale-heartbeat");
        exhausted.Events.Should().ContainSingle(operationEvent => operationEvent.MessageKey == "background-operation.failed"
            && operationEvent.EventType == NhBackgroundOperationEventType.StateChanged);
        (await persistence.GetFailedAttemptCountAsync(operationId, CancellationToken.None)).Should().Be(retryCount + 1);

        // Nothing is retried or changed after that.
        (await reconciler.ReconcileAsync(CancellationToken.None)).Should().Be(0);
        (await ReadOperationAsync(services, operationId)).Version.Should().Be(exhausted.Version);
    }

    /// <summary>
    /// A fan-out parent waits for its children when it is cancelled. The worker of one child stopped
    /// while the cancellation reached it: reconciliation ends that child cancelled, which reaches the
    /// parent as the worker's own end would, and the parent ends cancelled in the same round.
    /// </summary>
    [Theory]
    [InlineData(RelationalTestProvider.SqlServer)]
    [InlineData(RelationalTestProvider.PostgreSql)]
    public async Task A_fan_out_parent_ends_cancelled_once_the_child_whose_worker_stopped_is_cancelled(RelationalTestProvider provider)
    {
        await using var services = await CreateServicesAsync(provider);
        var options = services.GetRequiredService<NhBackgroundOperationsOptions>();
        var reconciler = services.GetRequiredService<NhBackgroundOperationReconciliationService>();
        var parentId = await EnqueueAsync(services, new StaleParentRequest());
        var parentClaim = await StartAttemptAsync(services, parentId);
        var parentContext = new NhBackgroundOperationContext(
            parentClaim,
            services.GetRequiredService<NhBackgroundOperationPersistence>(),
            options,
            new NhBackgroundOperationLeaseManager(services.GetRequiredService<IServiceScopeFactory>(), options, parentClaim),
            services.GetRequiredService<NhBackgroundOperationFanOutCoordinator>());
        var fanOut = () => parentContext.FanOut.RunAsync(
            "children",
            Enumerable.Range(1, 2).Select(index => NhBackgroundOperationFanOut.Item($"child-{index}", new StaleChildRequest(index))).ToArray());
        await fanOut.Should().ThrowAsync<NhBackgroundOperationFanOutPendingException>();
        (await ReadOperationAsync(services, parentId)).Status.Should().Be(NhBackgroundOperationStatus.WaitingForChildren);
        var children = await ReadChildrenAsync(services, parentId);
        children.Should().HaveCount(2);

        // The first child runs; the second still waits for a worker.
        var runningChild = children[0].Id;
        var waitingChild = children[1].Id;
        await services.GetRequiredService<NhBackgroundOperationDispatchService>().DispatchAvailableAsync(CancellationToken.None);
        var childClaim = await StartAttemptAsync(services, runningChild);
        await RequestCancellationAsync(services, parentId);
        (await ReadOperationAsync(services, parentId)).Status.Should().Be(NhBackgroundOperationStatus.CancelRequested);
        var requestedChild = await ReadOperationAsync(services, runningChild);
        requestedChild.Status.Should().Be(NhBackgroundOperationStatus.CancelRequested);
        requestedChild.CurrentAttemptId.Should().Be(childClaim.AttemptId);

        // The running child's worker stopped.
        await BackdateHeartbeatAsync(services, runningChild, options.StaleAttemptTimeout + TimeSpan.FromMinutes(1));
        (await CreateHealthCheck(services).CheckHealthAsync(new HealthCheckContext())).Data["staleAttempts"].Should().Be(1);

        await reconciler.ReconcileAsync(CancellationToken.None);

        var stopped = await ReadOperationAsync(services, runningChild);
        stopped.Status.Should().Be(NhBackgroundOperationStatus.Cancelled);
        stopped.Events.Should().ContainSingle(operationEvent => operationEvent.MessageKey == "background-operation.cancelled");
        (await ReadOperationAsync(services, waitingChild)).Status.Should().Be(NhBackgroundOperationStatus.Cancelled);
        var parent = await ReadOperationAsync(services, parentId);
        parent.Status.Should().Be(NhBackgroundOperationStatus.Cancelled);
        parent.Steps.Single(step => step.StepKey == "root").Status.Should().Be(NhBackgroundOperationStepStatus.Cancelled);
        parent.Events.Should().ContainSingle(operationEvent => operationEvent.MessageKey == "background-operation.cancelled");
        (await CreateHealthCheck(services).CheckHealthAsync(new HealthCheckContext())).Status.Should().Be(HealthStatus.Healthy);

        // Nothing is left for a further round.
        (await reconciler.ReconcileAsync(CancellationToken.None)).Should().Be(0);
        (await ReadOperationAsync(services, parentId)).Version.Should().Be(parent.Version);
    }

    private static NhBackgroundOperationHealthCheck CreateHealthCheck(IServiceProvider services)
    {
        return new NhBackgroundOperationHealthCheck(
            services.GetRequiredService<IServiceScopeFactory>(),
            services.GetRequiredService<NhBackgroundOperationsOptions>(),
            services.GetRequiredService<TimeProvider>());
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

    private static async Task<Guid> CreateUserAsync(IServiceProvider services)
    {
        var userId = Guid.NewGuid();
        await using var scope = services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<StaleCancellationDbContext>();
        context.Users.Add(new NhUser
        {
            Id = userId,
            UserName = $"owner-{userId:N}@example.test",
            NormalizedUserName = $"OWNER-{userId:N}@EXAMPLE.TEST"
        });
        await context.SaveChangesAsync();
        return userId;
    }

    /// <summary>
    /// Dispatches the operation and starts a worker's attempt on it, as the runner does.
    /// </summary>
    private static async Task<NhBackgroundOperationAttemptClaim> StartAttemptAsync(IServiceProvider services, Guid operationId)
    {
        await services.GetRequiredService<NhBackgroundOperationDispatchService>().DispatchAvailableAsync(CancellationToken.None);
        var queued = await ReadOperationAsync(services, operationId);
        queued.Status.Should().Be(NhBackgroundOperationStatus.Queued);
        var claim = await services.GetRequiredService<NhBackgroundOperationPersistence>()
            .TryStartAttemptAsync(operationId, queued.DispatchGeneration, CancellationToken.None);
        claim.Should().NotBeNull();
        return claim!;
    }

    private static async Task<INhBackgroundOperationLeaseHandle> AcquireLeaseAsync(IServiceProvider services, NhBackgroundOperationAttemptClaim claim)
    {
        var lease = await new NhBackgroundOperationLeaseManager(
                services.GetRequiredService<IServiceScopeFactory>(),
                services.GetRequiredService<NhBackgroundOperationsOptions>(),
                claim)
            .AcquireAsync(ResourceKey, new NhBackgroundOperationLeaseOptions { LeaseDuration = TimeSpan.FromHours(1) });
        lease.Should().NotBeNull();
        return lease!;
    }

    /// <summary>
    /// An administrator cancels the operation and, with it, every operation below it.
    /// </summary>
    private static async Task RequestCancellationAsync(IServiceProvider services, Guid operationId)
    {
        await using var scope = services.CreateAsyncScope();
        INhBackgroundOperationAdministrationService administration = scope.ServiceProvider.GetRequiredService<NhBackgroundOperationService>();
        var requested = await administration.RequestCancellationAsync(operationId, Guid.NewGuid(), null, CancellationToken.None);
        requested.Success.Should().BeTrue();
    }

    /// <summary>
    /// The operation's last heartbeat, as its worker sent it <paramref name="age"/> ago on the system
    /// clock.
    /// </summary>
    private static async Task BackdateHeartbeatAsync(IServiceProvider services, Guid operationId, TimeSpan age)
    {
        await using var scope = services.CreateAsyncScope();
        var heartbeat = DateTimeOffset.UtcNow - age;
        await scope.ServiceProvider.GetRequiredService<StaleCancellationDbContext>().BackgroundOperations
            .Where(x => x.Id == operationId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.HeartbeatAt, heartbeat));
    }

    private static async Task<NhBackgroundOperation> ReadOperationAsync(IServiceProvider services, Guid operationId)
    {
        await using var scope = services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<StaleCancellationDbContext>().BackgroundOperations
            .AsNoTracking()
            .Include(x => x.Steps)
            .Include(x => x.Events)
            .Include(x => x.Attempts)
            .AsSplitQuery()
            .SingleAsync(x => x.Id == operationId);
    }

    private static async Task<IReadOnlyList<NhBackgroundOperation>> ReadChildrenAsync(IServiceProvider services, Guid parentId)
    {
        await using var scope = services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<StaleCancellationDbContext>().BackgroundOperations
            .AsNoTracking()
            .Where(x => x.ParentOperationId == parentId)
            .OrderBy(x => x.FanOutItemKey)
            .ToListAsync();
    }

    private static async Task<NhBackgroundOperationLease> ReadLeaseAsync(IServiceProvider services)
    {
        await using var scope = services.CreateAsyncScope();
        var resourceKey = NhBackgroundOperationKeys.HashResourceKey(ResourceKey);
        return await scope.ServiceProvider.GetRequiredService<StaleCancellationDbContext>().BackgroundOperationLeases
            .AsNoTracking()
            .SingleAsync(x => x.ResourceKey == resourceKey);
    }

    /// <summary>
    /// Builds the background-operation components with dependency injection, as an application
    /// does, and creates the database. Notification projection and live updates are recorded.
    /// </summary>
    private static async Task<ServiceProvider> CreateServicesAsync(RelationalTestProvider provider)
    {
        var database = await ProviderTestDatabase.CreateAsync(provider);
        var options = new NhBackgroundOperationsOptions
        {
            LiveUpdatesEnabled = false,
            HeartbeatInterval = TimeSpan.FromMilliseconds(100),
            DefaultLeaseDuration = TimeSpan.FromSeconds(2),
            TransactionLockTimeoutMilliseconds = 250
        };
        var registryBuilder = new NhBackgroundOperationBuilder(new ServiceCollection(), options);
        registryBuilder.Add<StaleSimpleRequest, StaleSimpleHandler>("stale-simple");
        registryBuilder.Add<StaleParentRequest, StaleParentHandler>("stale-parent");
        registryBuilder.Add<StaleChildRequest, StaleChildHandler>("stale-child");
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<StaleCancellationDbContext>(database.UseNewHeapProvider);
        services.AddScoped<IRepository<NhBackgroundOperation>>(serviceProvider =>
            new Repository<NhBackgroundOperation>(serviceProvider.GetRequiredService<StaleCancellationDbContext>(), serviceProvider));
        services.AddScoped<IRepository<NhBackgroundOperationLease>>(serviceProvider =>
            new Repository<NhBackgroundOperationLease>(serviceProvider.GetRequiredService<StaleCancellationDbContext>(), serviceProvider));
        services.AddScoped<IRepository<NhBackgroundOperationAttempt>>(serviceProvider =>
            new Repository<NhBackgroundOperationAttempt>(serviceProvider.GetRequiredService<StaleCancellationDbContext>(), serviceProvider));
        services.AddSingleton(options);
        services.AddSingleton(registryBuilder.Build());
        services.AddSingleton<INhHangfireQueueNameResolver, NhHangfireQueueNameResolver>();
        services.AddSingleton<INhBackgroundOperationScheduler, NoOpScheduler>();
        services.AddSingleton<RecordingLiveUpdates>();
        services.AddSingleton<INhBackgroundOperationLiveUpdatePublisher>(serviceProvider => serviceProvider.GetRequiredService<RecordingLiveUpdates>());
        services.AddSingleton<RecordingNotificationProjector>();
        services.AddSingleton<INhBackgroundOperationNotificationProjector>(serviceProvider => serviceProvider.GetRequiredService<RecordingNotificationProjector>());
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
        var serviceProvider = services.BuildServiceProvider();
        await using (var scope = serviceProvider.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<StaleCancellationDbContext>().Database.EnsureCreatedAsync();
        }

        return serviceProvider;
    }

    private sealed record StaleSimpleRequest(string Value);

    private sealed record StaleParentRequest;

    private sealed record StaleChildRequest(int Index);

    private sealed class StaleSimpleHandler : INhBackgroundOperationHandler<StaleSimpleRequest>
    {
        public Task<TaskResult> ExecuteAsync(StaleSimpleRequest request, INhBackgroundOperationContext context, CancellationToken cancellationToken)
        {
            return Task.FromResult(TaskResult.Succeeded());
        }
    }

    private sealed class StaleParentHandler : INhBackgroundOperationHandler<StaleParentRequest>
    {
        public Task<TaskResult> ExecuteAsync(StaleParentRequest request, INhBackgroundOperationContext context, CancellationToken cancellationToken)
        {
            return Task.FromResult(TaskResult.Succeeded());
        }
    }

    private sealed class StaleChildHandler : INhBackgroundOperationHandler<StaleChildRequest>
    {
        public Task<TaskResult> ExecuteAsync(StaleChildRequest request, INhBackgroundOperationContext context, CancellationToken cancellationToken)
        {
            return Task.FromResult(TaskResult.Succeeded());
        }
    }

    private sealed class RecordingLiveUpdates : INhBackgroundOperationLiveUpdatePublisher
    {
        private readonly List<Guid> _published = [];

        public IReadOnlyList<Guid> Published
        {
            get
            {
                lock (_published)
                {
                    return _published.ToArray();
                }
            }
        }

        public void Clear()
        {
            lock (_published)
            {
                _published.Clear();
            }
        }

        public Task PublishChangedAsync(
            Guid ownerUserId,
            NhBackgroundOperationChangedMessage message,
            CancellationToken cancellationToken = default)
        {
            lock (_published)
            {
                _published.Add(message.OperationId);
            }

            return Task.CompletedTask;
        }
    }

    private sealed class RecordingNotificationProjector : INhBackgroundOperationNotificationProjector
    {
        private readonly List<Guid> _projected = [];

        public IReadOnlyList<Guid> Projected
        {
            get
            {
                lock (_projected)
                {
                    return _projected.ToArray();
                }
            }
        }

        public void Clear()
        {
            lock (_projected)
            {
                _projected.Clear();
            }
        }

        public Task<TaskResult> ProjectAsync(Guid operationId, CancellationToken cancellationToken = default)
        {
            lock (_projected)
            {
                _projected.Add(operationId);
            }

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

    private sealed class StaleCancellationDbContext(DbContextOptions<StaleCancellationDbContext> options)
        : NhIdentityDbContext(options);
}
