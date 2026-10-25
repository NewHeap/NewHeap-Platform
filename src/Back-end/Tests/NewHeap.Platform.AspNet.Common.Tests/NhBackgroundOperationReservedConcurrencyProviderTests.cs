using System.Collections.Concurrent;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.AspNet.Common.DAL.Entities;
using NewHeap.Platform.AspNet.Common.Services.BackgroundOperations;
using NewHeap.Platform.Common.Models;
using NewHeap.Platform.Tests.Relational;
using Xunit;

namespace NewHeap.Platform.AspNet.Common.Tests;

/// <summary>
/// Reserved queue concurrency within the global limit, on both relational providers. The global
/// limit is three and the agents queue reserves one slot, so two slots are shared. Work on another
/// queue never takes the reserved slot, and agent work takes its reserved slot first and free shared
/// slots next, up to the limit of its own queue. Operations hold their slot until the test releases
/// them; an operation that finds no slot is rescheduled as contention.
/// </summary>
public sealed class NhBackgroundOperationReservedConcurrencyProviderTests
{
    private const string AgentQueue = "agents";
    private const string AgentOperationType = "reserved-agent-work";
    private const string OtherOperationType = "reserved-other-work";

    [Theory]
    [InlineData(RelationalTestProvider.SqlServer)]
    [InlineData(RelationalTestProvider.PostgreSql)]
    public async Task Work_on_other_queues_never_takes_the_reserved_slot(RelationalTestProvider provider)
    {
        await using var host = await CreateHostAsync(provider, agentQueueLimit: null);
        var gate = host.Services.GetRequiredService<Gate>();
        var owner = await host.CreateUserAsync();

        // Two operations of another queue fill both shared slots and keep them.
        var firstOther = await StartHeldAsync(host, new OtherRequest(1), owner, gate, 1);
        var secondOther = await StartHeldAsync(host, new OtherRequest(2), owner, gate, 2);

        // A third finds only the agents queue's reserved slot free and waits for a shared one.
        var thirdOther = await RunToEndAsync(host, new OtherRequest(3), owner);
        await AssertBusyAsync(host, thirdOther);
        gate.HasStarted(3).Should().BeFalse();

        // Agent work still runs, in its reserved slot.
        var agent = await StartHeldAsync(host, new AgentRequest(101), owner, gate, 101);

        gate.ReleaseAll();
        await Task.WhenAll(firstOther.Run, secondOther.Run, agent.Run).WaitAsync(BackgroundOperationProviderHost.Patience);
        (await host.ReadOperationAsync(firstOther.OperationId)).Status.Should().Be(NhBackgroundOperationStatus.Succeeded);
        (await host.ReadOperationAsync(secondOther.OperationId)).Status.Should().Be(NhBackgroundOperationStatus.Succeeded);
        var agentOperation = await host.ReadOperationAsync(agent.OperationId);
        agentOperation.Status.Should().Be(NhBackgroundOperationStatus.Succeeded);
        agentOperation.Queue.Should().Be(AgentQueue);
    }

    [Theory]
    [InlineData(RelationalTestProvider.SqlServer)]
    [InlineData(RelationalTestProvider.PostgreSql)]
    public async Task Agent_work_takes_free_shared_slots_up_to_its_queue_limit_and_leaves_the_rest(RelationalTestProvider provider)
    {
        await using var host = await CreateHostAsync(provider, agentQueueLimit: 2);
        var gate = host.Services.GetRequiredService<Gate>();
        var owner = await host.CreateUserAsync();

        // The first agent operation takes the reserved slot, the second a shared one.
        var firstAgent = await StartHeldAsync(host, new AgentRequest(101), owner, gate, 101);
        var secondAgent = await StartHeldAsync(host, new AgentRequest(102), owner, gate, 102);

        // A third reaches the limit of its queue, although a shared slot is still free.
        var thirdAgent = await RunToEndAsync(host, new AgentRequest(103), owner);
        await AssertBusyAsync(host, thirdAgent);
        gate.HasStarted(103).Should().BeFalse();

        // That shared slot is left for other work; with it taken, the global limit is reached.
        var other = await StartHeldAsync(host, new OtherRequest(1), owner, gate, 1);
        var secondOther = await RunToEndAsync(host, new OtherRequest(2), owner);
        await AssertBusyAsync(host, secondOther);
        gate.HasStarted(2).Should().BeFalse();

        gate.ReleaseAll();
        await Task.WhenAll(firstAgent.Run, secondAgent.Run, other.Run).WaitAsync(BackgroundOperationProviderHost.Patience);
        foreach (var operationId in new[] { firstAgent.OperationId, secondAgent.OperationId, other.OperationId })
        {
            (await host.ReadOperationAsync(operationId)).Status.Should().Be(NhBackgroundOperationStatus.Succeeded);
        }
    }

    private static Task<BackgroundOperationProviderHost> CreateHostAsync(RelationalTestProvider provider, int? agentQueueLimit)
    {
        return BackgroundOperationProviderHost.CreateAsync(
            provider,
            operations =>
            {
                operations.WithGlobalConcurrency(3);
                if (agentQueueLimit is { } limit)
                {
                    operations.WithQueueConcurrency(AgentQueue, limit);
                }

                operations.WithReservedQueueConcurrency(AgentQueue, 1);
                operations.Add<AgentRequest, AgentHandler>(AgentOperationType, operation => operation.UseQueue(AgentQueue));
                operations.Add<OtherRequest, OtherHandler>(OtherOperationType);
            },
            services =>
            {
                services.AddSingleton<Gate>();
                services.AddTransient<AgentHandler>();
                services.AddTransient<OtherHandler>();
            });
    }

    /// <summary>
    /// Enqueues, dispatches and runs the operation until its handler holds the slot it took.
    /// </summary>
    private static async Task<HeldOperation> StartHeldAsync<TRequest>(
        BackgroundOperationProviderHost host,
        TRequest request,
        Guid owner,
        Gate gate,
        int number)
        where TRequest : notnull
    {
        var operationId = await host.EnqueueAsync(request, owner);
        var generation = await host.DispatchAsync(operationId);
        var run = host.CreateRunner(new CapturingLogger<NhBackgroundOperationRunner>()).RunAsync(operationId, generation);
        await Task.WhenAny(gate.StartedFor(number).Task, run).Unwrap().WaitAsync(BackgroundOperationProviderHost.Patience);
        gate.HasStarted(number).Should().BeTrue("operation {0} should hold a slot", number);
        return new HeldOperation(operationId, run);
    }

    private static async Task<Guid> RunToEndAsync<TRequest>(BackgroundOperationProviderHost host, TRequest request, Guid owner)
        where TRequest : notnull
    {
        var operationId = await host.EnqueueAsync(request, owner);
        var generation = await host.DispatchAsync(operationId);
        await host.CreateRunner(new CapturingLogger<NhBackgroundOperationRunner>())
            .RunAsync(operationId, generation)
            .WaitAsync(BackgroundOperationProviderHost.Patience);
        return operationId;
    }

    private static async Task AssertBusyAsync(BackgroundOperationProviderHost host, Guid operationId)
    {
        var operation = await host.ReadOperationAsync(operationId);
        operation.Status.Should().Be(NhBackgroundOperationStatus.RetryScheduled);
        operation.FailureCode.Should().Be("concurrency-busy");
        operation.Attempts.Should().ContainSingle()
            .Which.Status.Should().Be(NhBackgroundOperationAttemptStatus.Abandoned);
    }

    private sealed record HeldOperation(Guid OperationId, Task Run);

    private sealed record AgentRequest(int Number);

    private sealed record OtherRequest(int Number);

    /// <summary>
    /// Tells the test which handlers hold their slot and keeps them there until it releases them.
    /// </summary>
    private sealed class Gate
    {
        private readonly ConcurrentDictionary<int, TaskCompletionSource> _started = new();
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource StartedFor(int number)
        {
            return _started.GetOrAdd(number, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        }

        public bool HasStarted(int number)
        {
            return _started.TryGetValue(number, out var started) && started.Task.IsCompleted;
        }

        public async Task<TaskResult> HoldAsync(int number, CancellationToken cancellationToken)
        {
            StartedFor(number).TrySetResult();
            await _release.Task.WaitAsync(BackgroundOperationProviderHost.Patience, cancellationToken);
            return TaskResult.Succeeded();
        }

        public void ReleaseAll()
        {
            _release.TrySetResult();
        }
    }

    private sealed class AgentHandler(Gate gate) : INhBackgroundOperationHandler<AgentRequest>
    {
        public Task<TaskResult> ExecuteAsync(
            AgentRequest request,
            INhBackgroundOperationContext context,
            CancellationToken cancellationToken)
        {
            return gate.HoldAsync(request.Number, cancellationToken);
        }
    }

    private sealed class OtherHandler(Gate gate) : INhBackgroundOperationHandler<OtherRequest>
    {
        public Task<TaskResult> ExecuteAsync(
            OtherRequest request,
            INhBackgroundOperationContext context,
            CancellationToken cancellationToken)
        {
            return gate.HoldAsync(request.Number, cancellationToken);
        }
    }
}
