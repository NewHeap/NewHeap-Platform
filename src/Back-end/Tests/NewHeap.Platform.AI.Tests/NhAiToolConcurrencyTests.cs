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
    public async Task Default_limiter_admits_another_tenant_when_one_tenant_fills_the_tool_bound()
    {
        var services = new ServiceCollection();
        services.AddScoped<INhAiToolInvocationGate>(_ => new NhAiTestInvocationGate(
            (_, _) => ValueTask.FromResult(
                TaskResult<NhAiInvocationContext>.Succeeded(Context(CurrentTenant.Value)))));
        services.AddNewHeapPlatformAI(ai => ai.UseInMemoryBudget());
        await using var provider = services.BuildServiceProvider();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allRunning = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = 0;

        var tenantACalls = Enumerable.Range(0, MaxConcurrency)
            .Select(_ => InvokeAsTenantAsync(provider, "tenant-a", async () =>
            {
                if (Interlocked.Increment(ref running) == MaxConcurrency)
                {
                    allRunning.SetResult();
                }

                await release.Task;
                return "tenant-a";
            }))
            .ToArray();
        await allRunning.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var tenantB = await InvokeAsTenantAsync(provider, "tenant-b", () => Task.FromResult("tenant-b"));
        var tenantAOverflow = await InvokeAsTenantAsync(provider, "tenant-a", () => Task.FromResult("overflow"));
        release.SetResult();
        var tenantA = await Task.WhenAll(tenantACalls);

        Assert.True(tenantB.Success);
        Assert.Equal("tenant-b", tenantB.Data);
        Assert.False(tenantAOverflow.Success);
        Assert.Contains(
            tenantAOverflow.GetResultItems(),
            item => item.Name == NhAiToolFailureCodes.ConcurrencyLimited);
        Assert.All(tenantA, result => Assert.True(result.Success));
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

    private static async Task<TaskResult<string>> InvokeAsTenantAsync(
        IServiceProvider provider,
        string tenantId,
        Func<Task<string>> handler)
    {
        CurrentTenant.Value = tenantId;
        await using var scope = provider.CreateAsyncScope();
        var invoker = scope.ServiceProvider.GetRequiredService<INhAiToolInvoker>();

        return await invoker.InvokeAsync(
            Descriptor,
            async (_, _) => TaskResult<string>.Succeeded(await handler()));
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
}
