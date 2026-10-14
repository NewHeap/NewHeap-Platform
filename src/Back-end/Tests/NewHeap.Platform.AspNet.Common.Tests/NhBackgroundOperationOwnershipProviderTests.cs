using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NewHeap.Platform.AspNet.Common.DAL;
using NewHeap.Platform.AspNet.Common.DAL.Entities;
using NewHeap.Platform.AspNet.Common.PostgreSql;
using NewHeap.Platform.AspNet.Common.Services.BackgroundOperations;
using NewHeap.Platform.AspNet.Common.Services.Notification;
using NewHeap.Platform.AspNet.Common.SqlServer;
using NewHeap.Platform.AspNet.Common.Utilities;
using NewHeap.Platform.Common.Models;
using NewHeap.Platform.Common.Utilities;
using NewHeap.Platform.Mapping;
using NSubstitute;
using Testcontainers.MsSql;
using Testcontainers.PostgreSql;
using Xunit;

namespace NewHeap.Platform.AspNet.Common.Tests;

/// <summary>
/// Agent-owned operations, access policies for non-owners, delayed dispatch and enqueue
/// inside a caller transaction, on both relational providers.
/// </summary>
public sealed class NhBackgroundOperationOwnershipProviderTests
{
    private const string AgentKey = "assistant-agent:project-intake";
    private const string SubjectType = "project";
    private const string SubjectId = "alpha";

    [Fact]
    public async Task AgentOwnedOperationsFollowAccessPoliciesOnBothRelationalProviders()
    {
        await using (var sqlServer = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-CU14-ubuntu-22.04").Build())
        {
            await sqlServer.StartAsync();
            await VerifyProviderAsync(options => options.UseNewHeapSqlServer(sqlServer.GetConnectionString()));
        }

        await using (var postgreSql = new PostgreSqlBuilder("postgres:15.1").Build())
        {
            await postgreSql.StartAsync();
            await VerifyProviderAsync(options => options.UseNewHeapPostgreSql(postgreSql.GetConnectionString()));
        }
    }

    private static async Task VerifyProviderAsync(Action<DbContextOptionsBuilder> configureProvider)
    {
        var memberId = Guid.NewGuid();
        var outsiderId = Guid.NewGuid();
        var services = new ServiceCollection();
        services.AddLogging();
        // The signal service resolves access policies from its own scope, as an application
        // registers them through AddAccess.
        services.AddSingleton(new MemberAccessPolicy(memberId));
        new NhBackgroundOperationBuilder(services, new NhBackgroundOperationsOptions())
            .AddAccess<MemberAccessPolicyAdapter>();
        services.AddDbContext<OwnershipDbContext>(configureProvider);
        services.AddScoped<IRepository<NhBackgroundOperation>>(serviceProvider =>
            new Repository<NhBackgroundOperation>(serviceProvider.GetRequiredService<OwnershipDbContext>(), serviceProvider));
        services.AddScoped<IRepository<NhBackgroundOperationLease>>(serviceProvider =>
            new Repository<NhBackgroundOperationLease>(serviceProvider.GetRequiredService<OwnershipDbContext>(), serviceProvider));
        services.AddScoped<IRepository<NhBackgroundOperationAttempt>>(serviceProvider =>
            new Repository<NhBackgroundOperationAttempt>(serviceProvider.GetRequiredService<OwnershipDbContext>(), serviceProvider));
        services.AddScoped<OwnershipSignalHandler>();
        services.AddScoped<OwnershipSimpleHandler>();
        services.AddSingleton<INhBackgroundOperationScheduler, NoOpScheduler>();
        services.AddSingleton(Substitute.For<INhUserNotificationService>());
        services.AddSingleton<INhBackgroundOperationNotificationFormatter,
            NhDefaultBackgroundOperationNotificationFormatter>();
        services.AddScoped<INhBackgroundOperationNotificationPolicy, NhDefaultBackgroundOperationNotificationPolicy>();
        await using var serviceProvider = services.BuildServiceProvider();

        await using (var seedScope = serviceProvider.CreateAsyncScope())
        {
            var context = seedScope.ServiceProvider.GetRequiredService<OwnershipDbContext>();
            await context.Database.EnsureCreatedAsync();
            context.Users.AddRange(
                new NhUser { Id = memberId, UserName = "member@example.test", NormalizedUserName = "MEMBER@EXAMPLE.TEST" },
                new NhUser { Id = outsiderId, UserName = "outsider@example.test", NormalizedUserName = "OUTSIDER@EXAMPLE.TEST" });
            await context.SaveChangesAsync();
        }

        var options = new NhBackgroundOperationsOptions
        {
            UserNotificationProjectionEnabled = false,
            LiveUpdatesEnabled = false,
            HeartbeatInterval = TimeSpan.FromMilliseconds(100),
            DefaultLeaseDuration = TimeSpan.FromSeconds(2),
            TransactionLockTimeoutMilliseconds = 250
        };
        var registryBuilder = new NhBackgroundOperationBuilder(new ServiceCollection(), options);
        registryBuilder.Add<OwnershipSignalRequest, OwnershipSignalHandler>("ownership-signal");
        registryBuilder.Add<OwnershipSimpleRequest, OwnershipSimpleHandler>("ownership-simple");
        var registry = registryBuilder.Build();
        var accessPolicy = new MemberAccessPolicy(memberId);

        await VerifyOwnerValidationAsync(serviceProvider, registry, options, accessPolicy, memberId);
        await VerifyAgentOwnedVisibilityAsync(serviceProvider, registry, options, accessPolicy, memberId, outsiderId);
        await VerifyDelayedDispatchAsync(serviceProvider, registry, options, accessPolicy);
        await VerifyCallerTransactionAsync(serviceProvider, registry, options, accessPolicy, memberId);
        await VerifyAccessCheckedSignalAsync(serviceProvider, registry, options, memberId, outsiderId);
        await VerifyOwnerSignalAsync(serviceProvider, registry, options, memberId);
    }

    private static async Task VerifyOwnerValidationAsync(
        ServiceProvider serviceProvider,
        NhBackgroundOperationRegistry registry,
        NhBackgroundOperationsOptions options,
        MemberAccessPolicy accessPolicy,
        Guid memberId)
    {
        await using var scope = serviceProvider.CreateAsyncScope();
        var service = CreateService(scope, registry, options, accessPolicy, new RecordingLiveUpdates());

        var withoutOwner = await service.EnqueueAsync(
            new OwnershipSimpleRequest("none"),
            new NhBackgroundOperationEnqueueOptions());
        var withTwoOwners = await service.EnqueueAsync(
            new OwnershipSimpleRequest("both"),
            new NhBackgroundOperationEnqueueOptions { OwnerUserId = memberId, OwnerAgentKey = AgentKey });

        withoutOwner.Success.Should().BeFalse();
        withTwoOwners.Success.Should().BeFalse();
    }

    private static async Task VerifyAgentOwnedVisibilityAsync(
        ServiceProvider serviceProvider,
        NhBackgroundOperationRegistry registry,
        NhBackgroundOperationsOptions options,
        MemberAccessPolicy accessPolicy,
        Guid memberId,
        Guid outsiderId)
    {
        await using var scope = serviceProvider.CreateAsyncScope();
        var liveUpdates = new RecordingLiveUpdates();
        var service = CreateService(scope, registry, options, accessPolicy, liveUpdates);
        var concurrencyKey = $"agent:{AgentKey}:subject:{SubjectType}:{SubjectId}";

        var enqueued = await service.EnqueueAsync(
            new OwnershipSimpleRequest("agent"),
            new NhBackgroundOperationEnqueueOptions
            {
                OwnerAgentKey = AgentKey,
                DomainObjectType = SubjectType,
                DomainObjectId = SubjectId,
                IdempotencyKey = "trigger-event-1",
                ConcurrencyKey = concurrencyKey,
                ConflictBehavior = NhBackgroundOperationConflictBehavior.ReturnExisting
            });
        enqueued.Success.Should().BeTrue();
        var operationId = enqueued.Data!.Id;
        enqueued.Data.OwnerKind.Should().Be(NhBackgroundOperationOwnerKind.Agent);
        enqueued.Data.OwnerUserId.Should().BeNull();
        enqueued.Data.OwnerKey.Should().Be(AgentKey);
        liveUpdates.Published.Should().BeEmpty("an agent owner has no user channel");

        var duplicate = await service.EnqueueAsync(
            new OwnershipSimpleRequest("agent"),
            new NhBackgroundOperationEnqueueOptions
            {
                OwnerAgentKey = AgentKey,
                DomainObjectType = SubjectType,
                DomainObjectId = SubjectId,
                IdempotencyKey = "trigger-event-1"
            });
        duplicate.Success.Should().BeTrue();
        duplicate.Data!.Id.Should().Be(operationId);

        (await service.GetForUserAsync(operationId, memberId)).Should().NotBeNull();
        (await service.GetForUserAsync(operationId, outsiderId)).Should().BeNull();
        (await service.GetAsync(operationId, memberId)).Should().BeNull("the owner-only API ignores access policies");
        (await service.QueryForOwner(memberId).AnyAsync(x => x.Id == operationId)).Should().BeFalse();

        // A user-owned request for the same resource coalesces into the agent operation when
        // the policy lets the user see it, and is refused for everyone else.
        var memberConflict = await service.EnqueueAsync(
            new OwnershipSimpleRequest("member"),
            new NhBackgroundOperationEnqueueOptions
            {
                OwnerUserId = memberId,
                ConcurrencyKey = concurrencyKey,
                ConflictBehavior = NhBackgroundOperationConflictBehavior.ReturnExisting
            });
        memberConflict.Success.Should().BeTrue();
        memberConflict.Data!.Id.Should().Be(operationId);
        var outsiderConflict = await service.EnqueueAsync(
            new OwnershipSimpleRequest("outsider"),
            new NhBackgroundOperationEnqueueOptions
            {
                OwnerUserId = outsiderId,
                ConcurrencyKey = concurrencyKey,
                ConflictBehavior = NhBackgroundOperationConflictBehavior.ReturnExisting
            });
        outsiderConflict.Success.Should().BeFalse();

        var outsiderCancel = await service.RequestCancellationForUserAsync(operationId, outsiderId);
        outsiderCancel.Success.Should().BeFalse();
        var memberRetry = await service.RetryForUserAsync(operationId, memberId);
        memberRetry.Success.Should().BeFalse("the policy does not grant retry");
        var memberCancel = await service.RequestCancellationForUserAsync(operationId, memberId);
        memberCancel.Success.Should().BeTrue();
        memberCancel.Data!.Status.Should().Be(NhBackgroundOperationStatus.CancelRequested);
    }

    private static async Task VerifyDelayedDispatchAsync(
        ServiceProvider serviceProvider,
        NhBackgroundOperationRegistry registry,
        NhBackgroundOperationsOptions options,
        MemberAccessPolicy accessPolicy)
    {
        Guid operationId;
        var notBefore = DateTimeOffset.UtcNow.AddHours(1);
        await using (var scope = serviceProvider.CreateAsyncScope())
        {
            var service = CreateService(scope, registry, options, accessPolicy, new RecordingLiveUpdates());
            var enqueued = await service.EnqueueAsync(
                new OwnershipSimpleRequest("delayed"),
                new NhBackgroundOperationEnqueueOptions { OwnerAgentKey = AgentKey, NotBefore = notBefore });
            enqueued.Success.Should().BeTrue();
            operationId = enqueued.Data!.Id;
        }

        var dispatcher = CreateDispatcher(serviceProvider, registry, options);
        await dispatcher.DispatchAvailableAsync(CancellationToken.None);

        await using var verificationScope = serviceProvider.CreateAsyncScope();
        var context = verificationScope.ServiceProvider.GetRequiredService<OwnershipDbContext>();
        var operation = await context.BackgroundOperations.AsNoTracking().SingleAsync(x => x.Id == operationId);
        operation.Status.Should().Be(NhBackgroundOperationStatus.PendingDispatch);
        operation.NextDispatchAt.Should().BeCloseTo(notBefore, TimeSpan.FromSeconds(1));
    }

    private static async Task VerifyCallerTransactionAsync(
        ServiceProvider serviceProvider,
        NhBackgroundOperationRegistry registry,
        NhBackgroundOperationsOptions options,
        MemberAccessPolicy accessPolicy,
        Guid memberId)
    {
        Guid rolledBackOperationId;
        var liveUpdates = new RecordingLiveUpdates();
        await using (var scope = serviceProvider.CreateAsyncScope())
        {
            var repository = scope.ServiceProvider.GetRequiredService<IRepository<NhBackgroundOperation>>();
            var service = CreateService(scope, registry, options, accessPolicy, liveUpdates);
            await using var transaction = await repository.StartOrGetTransactionScopeAsync();
            var enqueued = await service.EnqueueAsync(
                new OwnershipSimpleRequest("rolled-back"),
                new NhBackgroundOperationEnqueueOptions { OwnerUserId = memberId });
            enqueued.Success.Should().BeTrue();
            rolledBackOperationId = enqueued.Data!.Id;
            liveUpdates.Published.Should().BeEmpty("the caller's transaction has not committed yet");
            await transaction.RollbackAsync();
        }

        await using (var verificationScope = serviceProvider.CreateAsyncScope())
        {
            var context = verificationScope.ServiceProvider.GetRequiredService<OwnershipDbContext>();
            (await context.BackgroundOperations.AnyAsync(x => x.Id == rolledBackOperationId)).Should().BeFalse();
        }

        await using (var scope = serviceProvider.CreateAsyncScope())
        {
            var service = CreateService(scope, registry, options, accessPolicy, liveUpdates);
            var enqueued = await service.EnqueueAsync(
                new OwnershipSimpleRequest("committed"),
                new NhBackgroundOperationEnqueueOptions { OwnerUserId = memberId });
            enqueued.Success.Should().BeTrue();
            liveUpdates.Published.Should().ContainSingle().Which.Should().Be(enqueued.Data!.Id);
        }
    }

    private static async Task VerifyAccessCheckedSignalAsync(
        ServiceProvider serviceProvider,
        NhBackgroundOperationRegistry registry,
        NhBackgroundOperationsOptions options,
        Guid memberId,
        Guid outsiderId)
    {
        var (operationId, persistence) = await SuspendAgentOperationAsync(serviceProvider, registry, options, memberId);
        var signalService = new NhBackgroundOperationSignalService(persistence);
        var signal = new OwnershipApprovalSignal(true);
        var asOwnerUser = await signalService.SignalForOwnerAsync(
            operationId,
            memberId,
            memberId,
            "ownership-approval",
            signal);
        asOwnerUser.Success.Should().BeFalse("the member does not own the agent operation");
        var asOutsider = await signalService.SignalAsync(operationId, outsiderId, "ownership-approval", signal);
        asOutsider.Success.Should().BeFalse();
        var asMember = await signalService.SignalAsync(operationId, memberId, "ownership-approval", signal);
        asMember.Success.Should().BeTrue(string.Join("; ", asMember.AllErrorMessages));
        asMember.Data!.Status.Should().Be(NhBackgroundOperationSignalWriteStatus.Accepted);

        await using var verificationScope = serviceProvider.CreateAsyncScope();
        var context = verificationScope.ServiceProvider.GetRequiredService<OwnershipDbContext>();
        var operation = await context.BackgroundOperations.AsNoTracking().SingleAsync(x => x.Id == operationId);
        operation.Status.Should().Be(NhBackgroundOperationStatus.PendingDispatch);
    }

    /// <summary>
    /// A platform module ends a wait it started itself on behalf of the agent that owns the
    /// operation, without a person and without access policies.
    /// </summary>
    private static async Task VerifyOwnerSignalAsync(
        ServiceProvider serviceProvider,
        NhBackgroundOperationRegistry registry,
        NhBackgroundOperationsOptions options,
        Guid memberId)
    {
        var (operationId, persistence) = await SuspendAgentOperationAsync(serviceProvider, registry, options, memberId);
        var ownerSignals = new NhBackgroundOperationOwnerSignals(persistence);

        var wrongWait = await ownerSignals.SignalAsync(operationId, "another-wait", new OwnershipApprovalSignal(false));
        wrongWait.Success.Should().BeFalse();
        var signaled = await ownerSignals.SignalAsync(operationId, "ownership-approval", new OwnershipApprovalSignal(false));
        signaled.Success.Should().BeTrue(string.Join("; ", signaled.AllErrorMessages));
        signaled.Data!.Status.Should().Be(NhBackgroundOperationSignalWriteStatus.Accepted);
        var repeated = await ownerSignals.SignalAsync(operationId, "ownership-approval", new OwnershipApprovalSignal(false));
        repeated.Data!.Status.Should().Be(NhBackgroundOperationSignalWriteStatus.Duplicate);

        await using var verificationScope = serviceProvider.CreateAsyncScope();
        var context = verificationScope.ServiceProvider.GetRequiredService<OwnershipDbContext>();
        var operation = await context.BackgroundOperations.AsNoTracking().SingleAsync(x => x.Id == operationId);
        operation.Status.Should().Be(NhBackgroundOperationStatus.PendingDispatch);
    }

    /// <summary>
    /// Enqueues an agent-owned operation and runs it until it waits for its signal.
    /// </summary>
    private static async Task<(Guid OperationId, NhBackgroundOperationPersistence Persistence)> SuspendAgentOperationAsync(
        ServiceProvider serviceProvider,
        NhBackgroundOperationRegistry registry,
        NhBackgroundOperationsOptions options,
        Guid memberId)
    {
        Guid operationId;
        await using (var scope = serviceProvider.CreateAsyncScope())
        {
            var service = CreateService(scope, registry, options, new MemberAccessPolicy(memberId), new RecordingLiveUpdates());
            var enqueued = await service.EnqueueAsync(
                new OwnershipSignalRequest(DateTimeOffset.UtcNow.AddMinutes(5)),
                new NhBackgroundOperationEnqueueOptions
                {
                    OwnerAgentKey = AgentKey,
                    DomainObjectType = SubjectType,
                    DomainObjectId = SubjectId
                });
            enqueued.Success.Should().BeTrue();
            operationId = enqueued.Data!.Id;
        }

        var scopeFactory = serviceProvider.GetRequiredService<IServiceScopeFactory>();
        var fanOutCoordinator = new NhBackgroundOperationFanOutCoordinator(
            scopeFactory,
            registry,
            options,
            new NhHangfireQueueNameResolver(),
            new RecordingLiveUpdates(),
            new NoOpNotificationProjector(),
            NullLogger<NhBackgroundOperationFanOutCoordinator>.Instance);
        var persistence = new NhBackgroundOperationPersistence(
            scopeFactory,
            options,
            new RecordingLiveUpdates(),
            new NoOpNotificationProjector(),
            fanOutCoordinator,
            NullLogger<NhBackgroundOperationPersistence>.Instance);
        var dispatchGeneration = await DispatchUntilQueuedAsync(serviceProvider, registry, options, operationId);
        var runner = new NhBackgroundOperationRunner(
            serviceProvider,
            scopeFactory,
            persistence,
            registry,
            fanOutCoordinator,
            options,
            NullLogger<NhBackgroundOperationRunner>.Instance);
        await runner.RunAsync(operationId, dispatchGeneration);
        await using (var suspendedScope = serviceProvider.CreateAsyncScope())
        {
            var suspendedContext = suspendedScope.ServiceProvider.GetRequiredService<OwnershipDbContext>();
            var suspended = await suspendedContext.BackgroundOperations.AsNoTracking().SingleAsync(x => x.Id == operationId);
            suspended.Status.Should().Be(NhBackgroundOperationStatus.WaitingForSignal);
        }

        return (operationId, persistence);
    }

    private static async Task<int> DispatchUntilQueuedAsync(
        ServiceProvider serviceProvider,
        NhBackgroundOperationRegistry registry,
        NhBackgroundOperationsOptions options,
        Guid operationId)
    {
        var dispatcher = CreateDispatcher(serviceProvider, registry, options);
        for (var round = 0; round < 20; round++)
        {
            await dispatcher.DispatchAvailableAsync(CancellationToken.None);
            await using var scope = serviceProvider.CreateAsyncScope();
            var context = scope.ServiceProvider.GetRequiredService<OwnershipDbContext>();
            var operation = await context.BackgroundOperations.AsNoTracking().SingleAsync(x => x.Id == operationId);
            if (operation.Status == NhBackgroundOperationStatus.Queued)
            {
                return operation.DispatchGeneration;
            }
        }

        throw new InvalidOperationException($"Operation {operationId} was never dispatched.");
    }

    private static NhBackgroundOperationService CreateService(
        AsyncServiceScope scope,
        NhBackgroundOperationRegistry registry,
        NhBackgroundOperationsOptions options,
        MemberAccessPolicy accessPolicy,
        RecordingLiveUpdates liveUpdates)
    {
        return new NhBackgroundOperationService(
            scope.ServiceProvider.GetRequiredService<IRepository<NhBackgroundOperation>>(),
            registry,
            options,
            new NhHangfireQueueNameResolver(),
            new NoOpScheduler(),
            liveUpdates,
            new NoOpNotificationProjector(),
            new Mapper(new MapperConfiguration(configuration =>
                configuration.AddProfile<AutomapperProfileConfiguration>())),
            NullLogger<NhBackgroundOperationService>.Instance,
            null,
            [accessPolicy]);
    }

    private static NhBackgroundOperationDispatchService CreateDispatcher(
        IServiceProvider serviceProvider,
        NhBackgroundOperationRegistry registry,
        NhBackgroundOperationsOptions options)
    {
        return new NhBackgroundOperationDispatchService(
            serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            options,
            new RecordingLiveUpdates(),
            new NoOpNotificationProjector(),
            new NhBackgroundOperationFanOutCoordinator(
                serviceProvider.GetRequiredService<IServiceScopeFactory>(),
                registry,
                options,
                new NhHangfireQueueNameResolver(),
                new RecordingLiveUpdates(),
                new NoOpNotificationProjector(),
                NullLogger<NhBackgroundOperationFanOutCoordinator>.Instance),
            new NhBackgroundOperationServedQueues(registry, new NhHangfireQueueNameResolver()),
            NullLogger<NhBackgroundOperationDispatchService>.Instance);
    }

    private sealed record OwnershipSimpleRequest(string Value);

    private sealed record OwnershipSignalRequest(DateTimeOffset ExpiresAt);

    private sealed record OwnershipApprovalSignal(bool Approved);

    private sealed class OwnershipSimpleHandler : INhBackgroundOperationHandler<OwnershipSimpleRequest>
    {
        public Task<TaskResult> ExecuteAsync(
            OwnershipSimpleRequest request,
            INhBackgroundOperationContext context,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(TaskResult.Succeeded());
        }
    }

    private sealed class OwnershipSignalHandler : INhBackgroundOperationHandler<OwnershipSignalRequest>
    {
        public async Task<TaskResult> ExecuteAsync(
            OwnershipSignalRequest request,
            INhBackgroundOperationContext context,
            CancellationToken cancellationToken)
        {
            var wait = await context.Suspension.WaitForSignalAsync<OwnershipApprovalSignal>(
                "ownership-approval",
                request.ExpiresAt,
                cancellationToken: cancellationToken);
            if (wait.Signal is not { Approved: true })
            {
                return TaskResult.Failed("approval-missing", "background-operation.approval-missing");
            }

            return TaskResult.Succeeded();
        }
    }

    /// <summary>
    /// Lets one member view, cancel and signal agent operations on the test subject, and
    /// grants nothing else.
    /// </summary>
    private sealed class MemberAccessPolicy(Guid memberId) : INhBackgroundOperationAccess
    {
        public ValueTask<bool> IsAllowedAsync(
            NhBackgroundOperationAccessTarget operation,
            NhBackgroundOperationActor actor,
            NhBackgroundOperationAccessAction action,
            CancellationToken cancellationToken = default)
        {
            var allowed = actor.UserId == memberId
                          && operation.OwnerKind == NhBackgroundOperationOwnerKind.Agent
                          && operation.DomainObjectType == SubjectType
                          && operation.DomainObjectId == SubjectId
                          && action != NhBackgroundOperationAccessAction.Retry;
            return ValueTask.FromResult(allowed);
        }
    }

    private sealed class MemberAccessPolicyAdapter(MemberAccessPolicy inner) : INhBackgroundOperationAccess
    {
        public ValueTask<bool> IsAllowedAsync(
            NhBackgroundOperationAccessTarget operation,
            NhBackgroundOperationActor actor,
            NhBackgroundOperationAccessAction action,
            CancellationToken cancellationToken = default)
        {
            return inner.IsAllowedAsync(operation, actor, action, cancellationToken);
        }
    }

    private sealed class RecordingLiveUpdates : INhBackgroundOperationLiveUpdatePublisher
    {
        public List<Guid> Published { get; } = [];

        public Task PublishChangedAsync(
            Guid ownerUserId,
            NhBackgroundOperationChangedMessage message,
            CancellationToken cancellationToken = default)
        {
            lock (Published)
            {
                Published.Add(message.OperationId);
            }

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

    private sealed class OwnershipDbContext(DbContextOptions<OwnershipDbContext> options)
        : NhIdentityDbContext(options);
}
