using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using NewHeap.Platform.AspNet.Common.DAL;
using NewHeap.Platform.AspNet.Common.DAL.Entities;
using NewHeap.Platform.AspNet.Common.Services.BackgroundOperations;
using NewHeap.Platform.Tests.Relational;
using Xunit;

namespace NewHeap.Platform.AspNet.Common.Tests;

/// <summary>
/// The <c>nh-background-operations</c> readiness check reports what reconciliation would take up:
/// an attempt whose heartbeat is stale on the system clock, and work that has been due for
/// <c>StaleAttemptTimeout</c> on the application's clock and unchanged for as long on the system
/// clock. Debounced and backed-off work, signal waits and fan-out rechecks that are not due, and a
/// jump of the application's clock, keep it healthy. On both relational providers.
/// </summary>
public sealed class NhBackgroundOperationHealthCheckProviderTests
{
    private const string OwnerKey = "assistant-agent:health-check";

    private static readonly NhBackgroundOperationStatus[] DueTimeStatuses =
    [
        NhBackgroundOperationStatus.PendingDispatch,
        NhBackgroundOperationStatus.RetryScheduled,
        NhBackgroundOperationStatus.WaitingForChildren,
        NhBackgroundOperationStatus.WaitingForSignal
    ];

    /// <summary>
    /// Work whose due time lies ahead, such as a 30-second proposal follow-up, a 2-minute trigger
    /// debounce, a long retry backoff, a signal wait for a decision and a fan-out recheck, is not
    /// overdue however long ago it was written; neither is work that became due a moment ago.
    /// </summary>
    [Theory]
    [InlineData(RelationalTestProvider.SqlServer)]
    [InlineData(RelationalTestProvider.PostgreSql)]
    public async Task Debounced_and_backed_off_work_keeps_the_check_healthy(RelationalTestProvider provider)
    {
        var clock = new HealthTestClock(new DateTimeOffset(2001, 2, 3, 4, 5, 0, TimeSpan.Zero));
        await using var services = await CreateServicesAsync(provider, clock);
        var written = TimeSpan.FromMinutes(5);
        await AddOperationAsync(services, NhBackgroundOperationStatus.PendingDispatch, clock.GetUtcNow().AddSeconds(30), written);
        await AddOperationAsync(services, NhBackgroundOperationStatus.PendingDispatch, clock.GetUtcNow().AddMinutes(2), written);
        await AddOperationAsync(services, NhBackgroundOperationStatus.RetryScheduled, clock.GetUtcNow().AddMinutes(10), written);
        await AddOperationAsync(services, NhBackgroundOperationStatus.WaitingForSignal, clock.GetUtcNow().AddHours(4), written);
        await AddOperationAsync(services, NhBackgroundOperationStatus.WaitingForChildren, clock.GetUtcNow().AddSeconds(30), written);
        // Due for less than the stale timeout: a dispatcher has not had the time to take it.
        await AddOperationAsync(services, NhBackgroundOperationStatus.PendingDispatch, clock.GetUtcNow().AddMinutes(-1), written);

        var result = await CheckAsync(services, clock);

        result.Status.Should().Be(HealthStatus.Healthy);
        result.Data["overdueDispatches"].Should().Be(0);
        result.Data["staleAttempts"].Should().Be(0);
    }

    /// <summary>
    /// Work that has been due for the stale timeout and that nothing touched for as long is
    /// overdue in every status that waits for a due time, without a due time as well, and a queued
    /// operation that no worker started is overdue by its last change alone.
    /// </summary>
    [Theory]
    [InlineData(RelationalTestProvider.SqlServer)]
    [InlineData(RelationalTestProvider.PostgreSql)]
    public async Task Genuinely_overdue_work_degrades_the_check(RelationalTestProvider provider)
    {
        var clock = new HealthTestClock(new DateTimeOffset(2001, 2, 3, 4, 5, 0, TimeSpan.Zero));
        await using var services = await CreateServicesAsync(provider, clock);
        var options = services.GetRequiredService<NhBackgroundOperationsOptions>();
        var overdueFor = options.StaleAttemptTimeout + TimeSpan.FromMinutes(1);
        foreach (var status in DueTimeStatuses)
        {
            await AddOperationAsync(services, status, clock.GetUtcNow() - overdueFor, overdueFor);
        }
        await AddOperationAsync(services, NhBackgroundOperationStatus.PendingDispatch, null, overdueFor);
        await AddOperationAsync(services, NhBackgroundOperationStatus.Queued, null, overdueFor);
        // Long due as well, but touched a moment ago: reconciliation would not take it yet.
        await AddOperationAsync(services, NhBackgroundOperationStatus.PendingDispatch, clock.GetUtcNow() - overdueFor, TimeSpan.FromSeconds(10));
        await AddOperationAsync(services, NhBackgroundOperationStatus.Queued, null, TimeSpan.FromSeconds(10));

        var result = await CheckAsync(services, clock);

        result.Status.Should().Be(HealthStatus.Degraded);
        result.Data["overdueDispatches"].Should().Be(DueTimeStatuses.Length + 2);
        result.Data["staleAttempts"].Should().Be(0);
    }

    /// <summary>
    /// Work written a moment ago stays healthy however far the application's clock jumps, as it
    /// does in a test that lets a proposal expire, and so does a running attempt whose heartbeat is
    /// fresh. Once nothing touched the work for the stale timeout of real time, it is overdue.
    /// </summary>
    [Theory]
    [InlineData(RelationalTestProvider.SqlServer)]
    [InlineData(RelationalTestProvider.PostgreSql)]
    public async Task A_clock_jump_alone_does_not_degrade_the_check(RelationalTestProvider provider)
    {
        var clock = new HealthTestClock(new DateTimeOffset(2001, 2, 3, 4, 5, 0, TimeSpan.Zero));
        await using var services = await CreateServicesAsync(provider, clock);
        var options = services.GetRequiredService<NhBackgroundOperationsOptions>();
        var ids = new List<Guid>();
        foreach (var status in DueTimeStatuses)
        {
            ids.Add(await AddOperationAsync(services, status, clock.GetUtcNow().AddMinutes(5), TimeSpan.Zero));
        }
        await AddOperationAsync(services, NhBackgroundOperationStatus.Running, null, TimeSpan.Zero, heartbeatAge: TimeSpan.Zero);

        for (var jump = 0; jump < 4; jump++)
        {
            clock.Advance(TimeSpan.FromMinutes(15));
            var result = await CheckAsync(services, clock);
            result.Status.Should().Be(HealthStatus.Healthy);
        }

        await BackdateLastModifiedAsync(services, ids, options.StaleAttemptTimeout + TimeSpan.FromMinutes(1));
        var overdue = await CheckAsync(services, clock);
        overdue.Status.Should().Be(HealthStatus.Degraded);
        overdue.Data["overdueDispatches"].Should().Be(DueTimeStatuses.Length);
        overdue.Data["staleAttempts"].Should().Be(0);
    }

    /// <summary>
    /// An attempt whose heartbeat is older than the stale timeout on the system clock, or that
    /// never sent one, degrades the check, also while a cancellation waits for it; a fresh heartbeat
    /// does not.
    /// </summary>
    [Theory]
    [InlineData(RelationalTestProvider.SqlServer)]
    [InlineData(RelationalTestProvider.PostgreSql)]
    public async Task Stale_running_attempts_degrade_the_check(RelationalTestProvider provider)
    {
        var clock = new HealthTestClock(new DateTimeOffset(2001, 2, 3, 4, 5, 0, TimeSpan.Zero));
        await using var services = await CreateServicesAsync(provider, clock);
        var stale = services.GetRequiredService<NhBackgroundOperationsOptions>().StaleAttemptTimeout + TimeSpan.FromMinutes(1);
        await AddOperationAsync(services, NhBackgroundOperationStatus.Running, null, TimeSpan.Zero, heartbeatAge: TimeSpan.FromSeconds(5));
        (await CheckAsync(services, clock)).Status.Should().Be(HealthStatus.Healthy);

        await AddOperationAsync(services, NhBackgroundOperationStatus.Running, null, stale, heartbeatAge: stale);
        await AddOperationAsync(services, NhBackgroundOperationStatus.Running, null, stale);
        await AddOperationAsync(services, NhBackgroundOperationStatus.CancelRequested, null, stale, heartbeatAge: stale);

        var result = await CheckAsync(services, clock);

        result.Status.Should().Be(HealthStatus.Degraded);
        result.Data["staleAttempts"].Should().Be(3);
        result.Data["overdueDispatches"].Should().Be(0);
    }

    private static async Task<HealthCheckResult> CheckAsync(ServiceProvider services, TimeProvider clock)
    {
        var healthCheck = new NhBackgroundOperationHealthCheck(
            services.GetRequiredService<IServiceScopeFactory>(),
            services.GetRequiredService<NhBackgroundOperationsOptions>(),
            clock);
        return await healthCheck.CheckHealthAsync(new HealthCheckContext());
    }

    /// <summary>
    /// Writes an operation as it stands after its last change <paramref name="age"/> ago on the
    /// system clock. A running or cancelling one gets an attempt, with a heartbeat
    /// <paramref name="heartbeatAge"/> ago or none.
    /// </summary>
    private static async Task<Guid> AddOperationAsync(
        ServiceProvider services,
        NhBackgroundOperationStatus status,
        DateTimeOffset? nextDispatchAt,
        TimeSpan age,
        TimeSpan? heartbeatAge = null)
    {
        var now = DateTimeOffset.UtcNow;
        var running = status is NhBackgroundOperationStatus.Running or NhBackgroundOperationStatus.CancelRequested;
        var operation = new NhBackgroundOperation
        {
            Id = Guid.NewGuid(),
            OperationType = "health-check-test",
            OwnerKind = NhBackgroundOperationOwnerKind.Agent,
            OwnerKey = OwnerKey,
            Status = status,
            NextDispatchAt = nextDispatchAt,
            CurrentAttemptId = running ? Guid.NewGuid() : null,
            HeartbeatAt = heartbeatAge is { } heartbeat ? now - heartbeat : null,
            CreationDateTime = now - age,
            LastModifiedDateTime = now - age,
            Version = 1
        };

        await using var scope = services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<HealthCheckDbContext>();
        context.BackgroundOperations.Add(operation);
        await context.SaveChangesAsync();
        return operation.Id;
    }

    private static async Task BackdateLastModifiedAsync(ServiceProvider services, IReadOnlyCollection<Guid> operationIds, TimeSpan age)
    {
        await using var scope = services.CreateAsyncScope();
        var lastModified = DateTimeOffset.UtcNow - age;
        await scope.ServiceProvider.GetRequiredService<HealthCheckDbContext>().BackgroundOperations
            .Where(x => operationIds.Contains(x.Id))
            .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.LastModifiedDateTime, lastModified));
    }

    private static async Task<ServiceProvider> CreateServicesAsync(RelationalTestProvider provider, TimeProvider clock)
    {
        var database = await ProviderTestDatabase.CreateAsync(provider);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<HealthCheckDbContext>(database.UseNewHeapProvider);
        services.AddScoped<IRepository<NhBackgroundOperation>>(serviceProvider =>
            new Repository<NhBackgroundOperation>(serviceProvider.GetRequiredService<HealthCheckDbContext>(), serviceProvider));
        services.AddSingleton(new NhBackgroundOperationsOptions());
        services.AddSingleton(clock);
        var serviceProvider = services.BuildServiceProvider();
        await using (var scope = serviceProvider.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<HealthCheckDbContext>().Database.EnsureCreatedAsync();
        }

        return serviceProvider;
    }

    /// <summary>
    /// The application's clock: it stands still until the test moves it.
    /// </summary>
    private sealed class HealthTestClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow()
        {
            return _now;
        }

        public void Advance(TimeSpan duration)
        {
            _now = _now.Add(duration);
        }
    }

    private sealed class HealthCheckDbContext(DbContextOptions<HealthCheckDbContext> options)
        : NhIdentityDbContext(options);
}
