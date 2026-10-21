using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NewHeap.Platform.AspNet.Common.DAL;
using NewHeap.Platform.AspNet.Common.DAL.Entities;
using NewHeap.Platform.AspNet.Common.Services.BackgroundOperations;
using NewHeap.Platform.AspNet.Common.Services.Notification;
using NewHeap.Platform.AspNet.Common.Utilities;
using NewHeap.Platform.Common.Models;
using NewHeap.Platform.Common.Utilities;
using NewHeap.Platform.Mapping;
using NewHeap.Platform.Tests.Relational;
using NSubstitute;
using Xunit;

namespace NewHeap.Platform.AspNet.Common.Tests;

/// <summary>
/// Agent-owned operations, access policies for non-owners, delayed dispatch on the application's
/// clock and enqueue inside a caller transaction, on both relational providers.
/// </summary>
public sealed class NhBackgroundOperationOwnershipProviderTests
{
    private const string AgentKey = "assistant-agent:project-intake";
    private const string SubjectType = "project";
    private const string SubjectId = "alpha";

    [Theory]
    [InlineData(RelationalTestProvider.SqlServer)]
    [InlineData(RelationalTestProvider.PostgreSql)]
    public async Task AgentOwnedOperationsFollowAccessPoliciesOnEachRelationalProvider(RelationalTestProvider provider)
    {
        var database = await ProviderTestDatabase.CreateAsync(provider);
        await VerifyProviderAsync(database.UseNewHeapProvider);
    }

    /// <summary>
    /// A registered <see cref="TimeProvider"/> decides when an operation is due: its not-before
    /// time, the dispatcher's check, stalled-dispatch recovery and the expiry of a signal wait. The
    /// clock starts long before the system clock, as a test clock often does, so mixing it with the
    /// system clock would either dispatch the debounced operation at once or never dispatch at all.
    /// </summary>
    [Theory]
    [InlineData(RelationalTestProvider.SqlServer)]
    [InlineData(RelationalTestProvider.PostgreSql)]
    public async Task DueTimesFollowTheApplicationClockOnEachRelationalProvider(RelationalTestProvider provider)
    {
        var database = await ProviderTestDatabase.CreateAsync(provider);
        var clock = new TestClock(new DateTimeOffset(2001, 2, 3, 4, 5, 0, TimeSpan.Zero));
        var options = CreateClockedOptions();
        var registry = CreateClockedRegistry(options);
        await using var serviceProvider = await CreateClockedServicesAsync(database, clock, options, registry);

        var start = clock.GetUtcNow();
        Guid immediateId;
        Guid debouncedId;
        await using (var scope = serviceProvider.CreateAsyncScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<NhBackgroundOperationService>();
            var immediate = await service.EnqueueAsync(
                new OwnershipSimpleRequest("immediate"),
                new NhBackgroundOperationEnqueueOptions { OwnerAgentKey = AgentKey });
            var debounced = await service.EnqueueAsync(
                new OwnershipSimpleRequest("debounced"),
                new NhBackgroundOperationEnqueueOptions { OwnerAgentKey = AgentKey, NotBefore = start.AddSeconds(30) });
            immediate.Success.Should().BeTrue();
            debounced.Success.Should().BeTrue();
            immediateId = immediate.Data!.Id;
            debouncedId = debounced.Data!.Id;
        }

        (await ReadOperationAsync(serviceProvider, immediateId)).NextDispatchAt.Should().Be(start);
        (await ReadOperationAsync(serviceProvider, debouncedId)).NextDispatchAt.Should().Be(start.AddSeconds(30));

        // The clock has not reached the debounce: only the immediate operation is dispatched, and
        // reconciliation does not take the waiting one, due long before the system clock's now,
        // for a dispatch that stalled.
        var dispatcher = serviceProvider.GetRequiredService<NhBackgroundOperationDispatchService>();
        var reconciler = serviceProvider.GetRequiredService<NhBackgroundOperationReconciliationService>();
        await dispatcher.DispatchAvailableAsync(CancellationToken.None);
        await reconciler.ReconcileAsync(CancellationToken.None);
        (await ReadOperationAsync(serviceProvider, immediateId)).Status.Should().Be(NhBackgroundOperationStatus.Queued);
        var waiting = await ReadOperationAsync(serviceProvider, debouncedId);
        waiting.Status.Should().Be(NhBackgroundOperationStatus.PendingDispatch);
        waiting.NextDispatchAt.Should().Be(start.AddSeconds(30));

        clock.Advance(TimeSpan.FromSeconds(30));
        await dispatcher.DispatchAvailableAsync(CancellationToken.None);
        (await ReadOperationAsync(serviceProvider, debouncedId)).Status.Should().Be(NhBackgroundOperationStatus.Queued);

        await VerifySignalWaitExpiresOnTheApplicationClockAsync(serviceProvider, registry, options, clock);
    }

    /// <summary>
    /// A jump of the application's clock makes an operation due without making its dispatch look
    /// stalled: reconciliation takes a dispatch for stalled only when it has been due for the stale
    /// timeout on the application's clock and nothing touched it for that long on the system clock.
    /// A dispatch that is really left alone past the timeout is still recovered, and the next round
    /// waits for the timeout of real time again, however far the application's clock moves.
    /// </summary>
    [Theory]
    [InlineData(RelationalTestProvider.SqlServer)]
    [InlineData(RelationalTestProvider.PostgreSql)]
    public async Task AClockJumpDoesNotMakeADueDispatchLookStalledOnEachRelationalProvider(RelationalTestProvider provider)
    {
        var database = await ProviderTestDatabase.CreateAsync(provider);
        var clock = new TestClock(new DateTimeOffset(2001, 2, 3, 4, 5, 0, TimeSpan.Zero));
        var options = CreateClockedOptions();
        var registry = CreateClockedRegistry(options);
        await using var serviceProvider = await CreateClockedServicesAsync(database, clock, options, registry);
        var reconciler = serviceProvider.GetRequiredService<NhBackgroundOperationReconciliationService>();

        Guid operationId;
        await using (var scope = serviceProvider.CreateAsyncScope())
        {
            var enqueued = await scope.ServiceProvider.GetRequiredService<NhBackgroundOperationService>().EnqueueAsync(
                new OwnershipSimpleRequest("debounced"),
                new NhBackgroundOperationEnqueueOptions { OwnerAgentKey = AgentKey, NotBefore = clock.GetUtcNow().AddSeconds(30) });
            enqueued.Success.Should().BeTrue();
            operationId = enqueued.Data!.Id;
        }

        // The clock jumps fifteen minutes, as a test that lets a proposal expire does, so the
        // operation has been due far longer than the stale timeout on the application's clock, but
        // it was enqueued a moment ago. Before a dispatcher reaches it, no round of reconciliation
        // takes it for a stalled dispatch, however often the clock jumps; it would fail after
        // MaxDispatchRecoveries rounds otherwise.
        for (var jump = 0; jump <= options.MaxDispatchRecoveries; jump++)
        {
            clock.Advance(TimeSpan.FromMinutes(15));
            (await reconciler.ReconcileAsync(CancellationToken.None)).Should().Be(0);
        }
        var due = await ReadOperationWithEventsAsync(serviceProvider, operationId);
        due.Status.Should().Be(NhBackgroundOperationStatus.PendingDispatch);
        due.Events.Should().NotContain(operationEvent => operationEvent.MessageKey == "background-operation.dispatch-stalled");

        // Left alone past the stale timeout of real time, it is a stalled dispatch: one round records
        // the operator diagnosis and dispatches it again. That round touched it, so a further jump
        // of the application's clock starts no second round.
        await BackdateLastModifiedAsync(serviceProvider, operationId, options.StaleAttemptTimeout + TimeSpan.FromMinutes(1));
        (await reconciler.ReconcileAsync(CancellationToken.None)).Should().Be(1);
        clock.Advance(TimeSpan.FromMinutes(15));
        (await reconciler.ReconcileAsync(CancellationToken.None)).Should().Be(0);
        var recovered = await ReadOperationWithEventsAsync(serviceProvider, operationId);
        recovered.Status.Should().Be(NhBackgroundOperationStatus.PendingDispatch);
        recovered.NextDispatchAt.Should().Be(clock.GetUtcNow() - TimeSpan.FromMinutes(15));
        recovered.Events.Count(operationEvent => operationEvent.MessageKey == "background-operation.dispatch-stalled").Should().Be(1);
        recovered.Events.Should().Contain(operationEvent => operationEvent.MessageKey == "background-operation.dispatch-recovered");
    }

    private static NhBackgroundOperationsOptions CreateClockedOptions()
    {
        return new NhBackgroundOperationsOptions
        {
            UserNotificationProjectionEnabled = false,
            LiveUpdatesEnabled = false,
            HeartbeatInterval = TimeSpan.FromMilliseconds(100),
            DefaultLeaseDuration = TimeSpan.FromSeconds(2),
            TransactionLockTimeoutMilliseconds = 250
        };
    }

    private static NhBackgroundOperationRegistry CreateClockedRegistry(NhBackgroundOperationsOptions options)
    {
        var registryBuilder = new NhBackgroundOperationBuilder(new ServiceCollection(), options);
        registryBuilder.Add<OwnershipSignalRequest, OwnershipSignalHandler>("ownership-signal");
        registryBuilder.Add<OwnershipSimpleRequest, OwnershipSimpleHandler>("ownership-simple");
        return registryBuilder.Build();
    }

    /// <summary>
    /// Builds every background-operation component with dependency injection, as in an
    /// application, so each receives the registered clock, and creates the database.
    /// </summary>
    private static async Task<ServiceProvider> CreateClockedServicesAsync(
        ProviderTestDatabase database,
        TestClock clock,
        NhBackgroundOperationsOptions options,
        NhBackgroundOperationRegistry registry)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<OwnershipDbContext>(database.UseNewHeapProvider);
        services.AddScoped<IRepository<NhBackgroundOperation>>(serviceProvider =>
            new Repository<NhBackgroundOperation>(serviceProvider.GetRequiredService<OwnershipDbContext>(), serviceProvider));
        services.AddScoped<IRepository<NhBackgroundOperationLease>>(serviceProvider =>
            new Repository<NhBackgroundOperationLease>(serviceProvider.GetRequiredService<OwnershipDbContext>(), serviceProvider));
        services.AddScoped<IRepository<NhBackgroundOperationAttempt>>(serviceProvider =>
            new Repository<NhBackgroundOperationAttempt>(serviceProvider.GetRequiredService<OwnershipDbContext>(), serviceProvider));
        services.AddScoped<OwnershipSignalHandler>();
        services.AddScoped<OwnershipSimpleHandler>();
        services.AddSingleton(options);
        services.AddSingleton(registry);
        services.AddSingleton<INhHangfireQueueNameResolver, NhHangfireQueueNameResolver>();
        services.AddSingleton<INhBackgroundOperationScheduler, NoOpScheduler>();
        services.AddSingleton<INhBackgroundOperationLiveUpdatePublisher, RecordingLiveUpdates>();
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
        services.AddSingleton<TimeProvider>(clock);
        var serviceProvider = services.BuildServiceProvider();
        await using (var seedScope = serviceProvider.CreateAsyncScope())
        {
            await seedScope.ServiceProvider.GetRequiredService<OwnershipDbContext>().Database.EnsureCreatedAsync();
        }

        return serviceProvider;
    }

    private static async Task<NhBackgroundOperation> ReadOperationWithEventsAsync(IServiceProvider serviceProvider, Guid operationId)
    {
        await using var scope = serviceProvider.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<OwnershipDbContext>();
        return await context.BackgroundOperations
            .AsNoTracking()
            .Include(x => x.Events)
            .SingleAsync(x => x.Id == operationId);
    }

    /// <summary>
    /// Makes the operation look untouched for <paramref name="age"/> of real time, as a dispatch
    /// that nobody picked up does.
    /// </summary>
    private static async Task BackdateLastModifiedAsync(IServiceProvider serviceProvider, Guid operationId, TimeSpan age)
    {
        await using var scope = serviceProvider.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<OwnershipDbContext>();
        var lastModified = DateTimeOffset.UtcNow - age;
        await context.BackgroundOperations
            .Where(x => x.Id == operationId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.LastModifiedDateTime, lastModified));
    }

    /// <summary>
    /// A signal wait sleeps until the application's clock reaches its expiry, and then wakes a new
    /// attempt that sees it expired.
    /// </summary>
    private static async Task VerifySignalWaitExpiresOnTheApplicationClockAsync(
        ServiceProvider serviceProvider,
        NhBackgroundOperationRegistry registry,
        NhBackgroundOperationsOptions options,
        TestClock clock)
    {
        var expiresAt = clock.GetUtcNow().AddMinutes(5);
        Guid operationId;
        await using (var scope = serviceProvider.CreateAsyncScope())
        {
            var enqueued = await scope.ServiceProvider.GetRequiredService<NhBackgroundOperationService>().EnqueueAsync(
                new OwnershipSignalRequest(expiresAt),
                new NhBackgroundOperationEnqueueOptions { OwnerAgentKey = AgentKey });
            enqueued.Success.Should().BeTrue();
            operationId = enqueued.Data!.Id;
        }

        var dispatcher = serviceProvider.GetRequiredService<NhBackgroundOperationDispatchService>();
        var runner = new NhBackgroundOperationRunner(
            serviceProvider,
            serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            serviceProvider.GetRequiredService<NhBackgroundOperationPersistence>(),
            registry,
            serviceProvider.GetRequiredService<NhBackgroundOperationFanOutCoordinator>(),
            options,
            NullLogger<NhBackgroundOperationRunner>.Instance,
            clock);
        await dispatcher.DispatchAvailableAsync(CancellationToken.None);
        var queued = await ReadOperationAsync(serviceProvider, operationId);
        queued.Status.Should().Be(NhBackgroundOperationStatus.Queued);
        await runner.RunAsync(operationId, queued.DispatchGeneration);
        var suspended = await ReadOperationAsync(serviceProvider, operationId);
        suspended.Status.Should().Be(NhBackgroundOperationStatus.WaitingForSignal);
        suspended.NextDispatchAt.Should().Be(expiresAt);

        await dispatcher.DispatchAvailableAsync(CancellationToken.None);
        (await ReadOperationAsync(serviceProvider, operationId)).Status.Should().Be(NhBackgroundOperationStatus.WaitingForSignal);

        clock.Advance(TimeSpan.FromMinutes(5));
        await dispatcher.DispatchAvailableAsync(CancellationToken.None);
        var woken = await ReadOperationAsync(serviceProvider, operationId);
        woken.Status.Should().Be(NhBackgroundOperationStatus.Queued);
        await runner.RunAsync(operationId, woken.DispatchGeneration);
        var expired = await ReadOperationAsync(serviceProvider, operationId);
        expired.Status.Should().Be(NhBackgroundOperationStatus.Failed);
        expired.FailureCode.Should().Be("approval-missing");
    }

    private static async Task<NhBackgroundOperation> ReadOperationAsync(IServiceProvider serviceProvider, Guid operationId)
    {
        await using var scope = serviceProvider.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<OwnershipDbContext>();
        return await context.BackgroundOperations.AsNoTracking().SingleAsync(x => x.Id == operationId);
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

    /// <summary>
    /// A clock that stands still until the test advances it.
    /// </summary>
    private sealed class TestClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow()
        {
            return _now;
        }

        internal void Advance(TimeSpan duration)
        {
            _now = _now.Add(duration);
        }
    }

    private sealed class OwnershipDbContext(DbContextOptions<OwnershipDbContext> options)
        : NhIdentityDbContext(options);
}
