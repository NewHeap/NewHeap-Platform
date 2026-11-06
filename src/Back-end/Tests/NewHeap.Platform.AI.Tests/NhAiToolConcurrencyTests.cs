using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.AI.Test;
using NewHeap.Platform.Common.Models;
using Xunit;

namespace NewHeap.Platform.AI.Tests;

public sealed class NhAiToolConcurrencyTests
{
    private const int MaxConcurrency = 4;

    private static readonly AsyncLocal<string?> CurrentTenant = new();

    [Fact]
    public async Task Default_limiter_admits_another_tenant_while_a_further_call_of_the_full_tenant_waits()
    {
        var clock = new ManualClock();
        await using var provider = CreateInvokerProvider(clock);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allRunning = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = 0;
        var mostRunning = 0;

        async Task<string> HoldAsync(string result)
        {
            var now = Interlocked.Increment(ref running);
            InterlockedMax(ref mostRunning, now);
            if (now == MaxConcurrency)
            {
                allRunning.TrySetResult();
            }

            await release.Task;
            Interlocked.Decrement(ref running);
            return result;
        }

        var tenantACalls = Enumerable.Range(0, MaxConcurrency)
            .Select(_ => InvokeAsTenantAsync(provider, "tenant-a", () => HoldAsync("tenant-a")))
            .ToArray();
        await allRunning.Task.WaitAsync(TimeSpan.FromSeconds(30));

        var tenantB = await InvokeAsTenantAsync(provider, "tenant-b", () => Task.FromResult("tenant-b"));
        // Every call starts a wait bound on the application's clock; the overflow call's is the next one.
        var overflowWaits = clock.TimersCreated(MaxConcurrency + 2);
        var tenantAOverflow = InvokeAsTenantAsync(provider, "tenant-a", () => HoldAsync("overflow"));
        await overflowWaits.WaitAsync(TimeSpan.FromSeconds(30));
        release.SetResult();
        var tenantA = await Task.WhenAll(tenantACalls);

        Assert.True(tenantB.Success);
        Assert.Equal("tenant-b", tenantB.Data);
        var overflow = await tenantAOverflow.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(overflow.Success);
        Assert.Equal("overflow", overflow.Data);
        Assert.All(tenantA, result => Assert.True(result.Success));
        Assert.Equal(MaxConcurrency, Volatile.Read(ref mostRunning));
    }

    [Fact]
    public async Task A_call_whose_bound_stays_full_is_refused_when_its_wait_ends_on_the_application_clock()
    {
        var clock = new ManualClock();
        await using var provider = CreateInvokerProvider(clock);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var overflowRan = false;

        var holding = InvokeAsTenantAsync(provider, "tenant-a", SingleSlotTool, async () =>
        {
            held.SetResult();
            await release.Task;
            return "held";
        });
        await held.Task.WaitAsync(TimeSpan.FromSeconds(30));
        var overflowWaits = clock.TimersCreated(2);
        var overflow = InvokeAsTenantAsync(provider, "tenant-a", SingleSlotTool, () =>
        {
            overflowRan = true;
            return Task.FromResult("overflow");
        });
        await overflowWaits.WaitAsync(TimeSpan.FromSeconds(30));

        // The wait ends at the tool's timeout on the application's clock; nothing waits in real time.
        clock.Advance(SingleSlotTool.Timeout + TimeSpan.FromSeconds(1));
        var refused = await overflow.WaitAsync(TimeSpan.FromSeconds(30));
        release.SetResult();

        Assert.False(refused.Success);
        Assert.Equal(NhAiToolFailureCodes.ConcurrencyLimited, Assert.Single(refused.GetResultItems()).Name);
        Assert.False(overflowRan);
        Assert.True((await holding).Success);
    }

    [Fact]
    public async Task A_caller_that_cancels_while_waiting_gets_a_cancellation_not_a_refusal()
    {
        var clock = new ManualClock();
        await using var provider = CreateInvokerProvider(clock);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();

        var holding = InvokeAsTenantAsync(provider, "tenant-a", SingleSlotTool, async () =>
        {
            held.SetResult();
            await release.Task;
            return "held";
        });
        await held.Task.WaitAsync(TimeSpan.FromSeconds(30));
        var overflowWaits = clock.TimersCreated(2);
        var overflow = InvokeAsTenantAsync(
            provider,
            "tenant-a",
            SingleSlotTool,
            () => Task.FromResult("overflow"),
            cancellation.Token);
        await overflowWaits.WaitAsync(TimeSpan.FromSeconds(30));
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => overflow.WaitAsync(TimeSpan.FromSeconds(30)));
        release.SetResult();
        Assert.True((await holding).Success);
    }

    [Fact]
    public async Task Waiting_calls_take_released_slots_first_come_first_served()
    {
        await using var provider = CreateLimiterProvider();
        var limiter = provider.GetRequiredService<INhAiToolConcurrencyLimiter>();
        var filled = await AcquireAsync(limiter, "tenant-a", MaxConcurrency);
        using var bound = new CancellationTokenSource();

        var first = limiter.WaitToAcquireAsync(Descriptor, Context("tenant-a"), bound.Token).AsTask();
        var second = limiter.WaitToAcquireAsync(Descriptor, Context("tenant-a"), bound.Token).AsTask();
        var otherTenant = await limiter.WaitToAcquireAsync(Descriptor, Context("tenant-b"), bound.Token);

        Assert.False(first.IsCompleted);
        Assert.False(second.IsCompleted);
        Assert.True(otherTenant.Acquired);

        await filled[0].Lease!.DisposeAsync();
        var firstDecision = await first.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(firstDecision.Acquired);
        Assert.False(second.IsCompleted);
        // The released slot went to the waiting call, so the bound is still full for a new call.
        Assert.False((await limiter.TryAcquireAsync(Descriptor, Context("tenant-a"))).Acquired);

        await filled[1].Lease!.DisposeAsync();
        Assert.True((await second.WaitAsync(TimeSpan.FromSeconds(30))).Acquired);
    }

    [Fact]
    public async Task A_wait_that_ends_leaves_the_line_without_taking_a_slot()
    {
        await using var provider = CreateLimiterProvider();
        var limiter = provider.GetRequiredService<INhAiToolConcurrencyLimiter>();
        var filled = await AcquireAsync(limiter, "tenant-a", MaxConcurrency);
        using var bound = new CancellationTokenSource();

        var waiting = limiter.WaitToAcquireAsync(Descriptor, Context("tenant-a"), bound.Token).AsTask();
        await bound.CancelAsync();
        var ended = await waiting.WaitAsync(TimeSpan.FromSeconds(30));
        await filled[0].Lease!.DisposeAsync();
        var readmitted = await limiter.TryAcquireAsync(Descriptor, Context("tenant-a"));
        var overflow = await limiter.TryAcquireAsync(Descriptor, Context("tenant-a"));

        Assert.False(ended.Acquired);
        Assert.Equal("concurrency-wait-ended", ended.Code);
        Assert.True(readmitted.Acquired);
        Assert.False(overflow.Acquired);
    }

    [Fact]
    public async Task Default_limiter_shares_one_bound_between_invocations_without_a_tenant()
    {
        await using var provider = CreateLimiterProvider();
        var limiter = provider.GetRequiredService<INhAiToolConcurrencyLimiter>();

        var tenantless = await AcquireAsync(limiter, null, MaxConcurrency);
        var blankTenant = await limiter.TryAcquireAsync(Descriptor, Context(" "));
        var tenant = await limiter.TryAcquireAsync(Descriptor, Context("tenant-a"));

        Assert.All(tenantless, decision => Assert.True(decision.Acquired));
        Assert.False(blankTenant.Acquired);
        Assert.Equal("concurrency-limit-reached", blankTenant.Code);
        Assert.True(tenant.Acquired);
    }

    [Fact]
    public async Task Tool_partition_shares_one_bound_across_tenants()
    {
        await using var provider = CreateLimiterProvider(ai => ai.UseInProcessConcurrencyLimiter(
            options => options.Partition = NhAiToolConcurrencyPartition.Tool));
        var limiter = provider.GetRequiredService<INhAiToolConcurrencyLimiter>();

        var tenantA = await AcquireAsync(limiter, "tenant-a", MaxConcurrency);
        var tenantB = await limiter.TryAcquireAsync(Descriptor, Context("tenant-b"));

        Assert.All(tenantA, decision => Assert.True(decision.Acquired));
        Assert.False(tenantB.Acquired);
    }

    [Fact]
    public async Task Released_lease_readmits_its_tenant_once()
    {
        await using var provider = CreateLimiterProvider();
        var limiter = provider.GetRequiredService<INhAiToolConcurrencyLimiter>();
        var filled = await AcquireAsync(limiter, "tenant-a", MaxConcurrency);

        await filled[0].Lease!.DisposeAsync();
        await filled[0].Lease!.DisposeAsync();
        var readmitted = await limiter.TryAcquireAsync(Descriptor, Context("tenant-a"));
        var overflow = await limiter.TryAcquireAsync(Descriptor, Context("tenant-a"));

        Assert.True(readmitted.Acquired);
        Assert.False(overflow.Acquired);
    }

    [Fact]
    public void Unsupported_partition_is_rejected_during_registration()
    {
        var services = new ServiceCollection();

        Assert.Throws<ArgumentOutOfRangeException>(() => services.AddNewHeapPlatformAI(
            ai => ai.UseInProcessConcurrencyLimiter(
                options => options.Partition = (NhAiToolConcurrencyPartition)42)));
    }

    private static ServiceProvider CreateInvokerProvider(TimeProvider clock)
    {
        var services = new ServiceCollection();
        services.AddSingleton(clock);
        services.AddScoped<INhAiToolInvocationGate>(_ => new NhAiTestInvocationGate(
            (_, _) => ValueTask.FromResult(
                TaskResult<NhAiInvocationContext>.Succeeded(Context(CurrentTenant.Value)))));
        services.AddNewHeapPlatformAI(ai => ai.UseInMemoryBudget());
        return services.BuildServiceProvider();
    }

    private static Task<TaskResult<string>> InvokeAsTenantAsync(
        IServiceProvider provider,
        string tenantId,
        Func<Task<string>> handler)
    {
        return InvokeAsTenantAsync(provider, tenantId, Descriptor, handler);
    }

    private static async Task<TaskResult<string>> InvokeAsTenantAsync(
        IServiceProvider provider,
        string tenantId,
        NhAiToolDescriptor descriptor,
        Func<Task<string>> handler,
        CancellationToken cancellationToken = default)
    {
        CurrentTenant.Value = tenantId;
        await using var scope = provider.CreateAsyncScope();
        var invoker = scope.ServiceProvider.GetRequiredService<INhAiToolInvoker>();

        return await invoker.InvokeAsync(
            descriptor,
            async (_, _) => TaskResult<string>.Succeeded(await handler()),
            cancellationToken);
    }

    private static async Task<NhAiConcurrencyDecision[]> AcquireAsync(
        INhAiToolConcurrencyLimiter limiter,
        string? tenantId,
        int count)
    {
        var decisions = new NhAiConcurrencyDecision[count];
        for (var index = 0; index < count; index++)
        {
            decisions[index] = await limiter.TryAcquireAsync(Descriptor, Context(tenantId));
        }

        return decisions;
    }

    private static ServiceProvider CreateLimiterProvider(Action<NhAiBuilder>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddNewHeapPlatformAI(configure);
        return services.BuildServiceProvider();
    }

    private static NhAiInvocationContext Context(string? tenantId)
    {
        return new NhAiInvocationContext(
            $"actor-{tenantId ?? "none"}",
            "concurrency",
            new Dictionary<string, string>())
        {
            TenantId = tenantId
        };
    }

    private static void InterlockedMax(ref int target, int value)
    {
        var current = Volatile.Read(ref target);
        while (value > current)
        {
            var observed = Interlocked.CompareExchange(ref target, value, current);
            if (observed == current)
            {
                return;
            }

            current = observed;
        }
    }

    private static readonly NhAiToolDescriptor Descriptor = new(
        "concurrency.read",
        1,
        "Read data under a bounded concurrency.",
        typeof(ReadInput),
        typeof(string),
        NhAiToolEffect.ReadOnly,
        NhAiToolExposure.Local,
        false,
        [])
    {
        MaxConcurrency = MaxConcurrency
    };

    private static readonly NhAiToolDescriptor SingleSlotTool = Descriptor with
    {
        Id = "concurrency.single",
        MaxConcurrency = 1,
        Timeout = TimeSpan.FromSeconds(30)
    };

    /// <summary>
    /// The application's clock of a test: it stands still until the test moves it, and runs the timers
    /// created on it, such as the wait bound of a tool call, when the test moves past their due time.
    /// </summary>
    private sealed class ManualClock : TimeProvider
    {
        private readonly object _gate = new();
        private readonly List<ManualTimer> _timers = [];
        private readonly List<(int Count, TaskCompletionSource Signal)> _created = [];
        private DateTimeOffset _now = new(2026, 1, 5, 9, 0, 0, TimeSpan.Zero);
        private int _timersCreated;

        public override DateTimeOffset GetUtcNow()
        {
            lock (_gate)
            {
                return _now;
            }
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(this, callback, state);
            timer.Change(dueTime, period);
            lock (_gate)
            {
                _timersCreated++;
                foreach (var waiter in _created.Where(waiter => waiter.Count <= _timersCreated).ToArray())
                {
                    _created.Remove(waiter);
                    waiter.Signal.TrySetResult();
                }
            }

            return timer;
        }

        /// <summary>Completes once <paramref name="count"/> timers have been created on this clock.</summary>
        public Task TimersCreated(int count)
        {
            lock (_gate)
            {
                if (_timersCreated >= count)
                {
                    return Task.CompletedTask;
                }

                var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _created.Add((count, signal));
                return signal.Task;
            }
        }

        public void Advance(TimeSpan duration)
        {
            ManualTimer[] due;
            lock (_gate)
            {
                _now += duration;
                due = _timers.Where(timer => timer.DueAt <= _now).ToArray();
                foreach (var timer in due)
                {
                    _timers.Remove(timer);
                }
            }

            foreach (var timer in due)
            {
                timer.Fire();
            }
        }

        private sealed class ManualTimer(ManualClock clock, TimerCallback callback, object? state) : ITimer
        {
            public DateTimeOffset DueAt { get; private set; }

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                lock (clock._gate)
                {
                    clock._timers.Remove(this);
                    if (dueTime == Timeout.InfiniteTimeSpan)
                    {
                        return true;
                    }

                    DueAt = clock._now + dueTime;
                    clock._timers.Add(this);
                    return true;
                }
            }

            public void Fire()
            {
                callback(state);
            }

            public void Dispose()
            {
                lock (clock._gate)
                {
                    clock._timers.Remove(this);
                }
            }

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
}
