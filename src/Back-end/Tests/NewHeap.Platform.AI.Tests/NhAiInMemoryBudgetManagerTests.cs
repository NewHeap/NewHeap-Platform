using NewHeap.Platform.AI;
using Xunit;

namespace NewHeap.Platform.AI.Tests;

public sealed class NhAiInMemoryBudgetManagerTests
{
    [Fact]
    public async Task Budget_is_isolated_per_actor_and_rejects_the_first_over_limit_reservation()
    {
        var manager = new NhAiInMemoryBudgetManager(new NhAiInMemoryBudgetOptions
        {
            MaxCalls = 2,
            MaxInputTokens = 10,
            MaxOutputTokens = 5,
            MaxEstimatedCost = 1m
        });

        var first = await manager.ReserveAsync(Request("actor-a", 1, 4, 2, .25m));
        var second = await manager.ReserveAsync(Request("actor-a", 1, 6, 3, .75m));
        var exhausted = await manager.ReserveAsync(Request("actor-a", 1, 0, 0, 0));
        var otherActor = await manager.ReserveAsync(Request("actor-b", 1, 10, 5, 1m));

        Assert.True(first.Success);
        Assert.True(second.Success);
        Assert.Equal(0, second.Data.Remaining.MaxCalls);
        Assert.False(exhausted.Success);
        Assert.Contains(exhausted.GetResultItems(), item => item.Name == NhAiInMemoryBudgetManager.ExhaustedCode);
        Assert.True(otherActor.Success);
    }

    [Fact]
    public async Task Budget_fails_closed_without_an_actor()
    {
        var manager = new NhAiInMemoryBudgetManager(new NhAiInMemoryBudgetOptions());
        var result = await manager.ReserveAsync(new NhAiBudgetRequest(Guid.NewGuid(), "test", 1, 0, 0, null));

        Assert.False(result.Success);
        Assert.Contains(result.GetResultItems(), item => item.Name == NhAiInMemoryBudgetManager.ActorRequiredCode);
    }

    private static NhAiBudgetRequest Request(
        string actorId,
        int calls,
        int inputTokens,
        int outputTokens,
        decimal cost)
    {
        return new NhAiBudgetRequest(
            Guid.NewGuid(),
            "test",
            calls,
            inputTokens,
            outputTokens,
            cost)
        {
            ActorId = actorId
        };
    }
}
