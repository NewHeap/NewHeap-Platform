using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.Common.Actors;
using NewHeap.Platform.Common.Models;
using Xunit;

namespace NewHeap.Platform.Common.Tests;

public sealed class NhActorTests
{
    [Fact]
    public void ActorReferencesCompareByKindAndIdAndExposeUserIds()
    {
        var userId = Guid.NewGuid();
        var user = NhActorRef.ForUser(userId);

        Assert.Equal(new NhActorRef(NhActorKinds.User, userId.ToString("D")), user);
        Assert.True(user.TryGetUserId(out var parsed));
        Assert.Equal(userId, parsed);
        Assert.Equal(userId, new NhActorContext(user, null).UserId);

        var agent = new NhActorRef(NhActorKinds.Agent, userId.ToString("D"));
        Assert.NotEqual(user, agent);
        Assert.False(agent.TryGetUserId(out _));
        Assert.Null(new NhActorContext(agent, null).UserId);
        Assert.Throws<ArgumentException>(() => new NhActorRef(NhActorKinds.User, " "));
    }

    [Fact]
    public async Task RetentionRunsParticipantsInOrderAndStopsAtTheFirstFailure()
    {
        var calls = new List<string>();
        var userId = Guid.NewGuid();
        var retention = new NhRetention(
        [
            new RecordingParticipant("notifications", 0, calls, succeed: true),
            new RecordingParticipant("operations", -100, calls, succeed: true)
        ]);

        Assert.True((await retention.ForgetUserAsync(userId)).Success);
        Assert.Equal(["operations", "notifications"], calls);

        calls.Clear();
        var failing = new NhRetention(
        [
            new RecordingParticipant("later", 10, calls, succeed: true),
            new RecordingParticipant("blocked", 0, calls, succeed: false)
        ]);
        var result = await failing.ForgetDivisionAsync(Guid.NewGuid());

        Assert.False(result.Success);
        Assert.Equal(["blocked"], calls);
    }

    [Fact]
    public void ARetentionParticipantIsRegisteredOnce()
    {
        var services = new ServiceCollection();
        services.AddNewHeapRetentionParticipant<SingletonParticipant>();
        services.AddNewHeapRetentionParticipant<SingletonParticipant>();

        using var provider = services.BuildServiceProvider();
        Assert.Single(provider.GetServices<INhRetentionParticipant>());
        Assert.IsType<NhRetention>(provider.GetRequiredService<INhRetention>());
    }

    private sealed class RecordingParticipant(string name, int order, List<string> calls, bool succeed)
        : INhRetentionParticipant
    {
        public int Order => order;

        public Task<TaskResult> ForgetUserAsync(Guid userId, CancellationToken cancellationToken)
        {
            return Record();
        }

        public Task<TaskResult> ForgetDivisionAsync(Guid divisionId, CancellationToken cancellationToken)
        {
            return Record();
        }

        private Task<TaskResult> Record()
        {
            calls.Add(name);
            return Task.FromResult(succeed ? TaskResult.Succeeded() : TaskResult.Failed("blocked", "blocked"));
        }
    }

    private sealed class SingletonParticipant : INhRetentionParticipant
    {
        public int Order => 0;

        public Task<TaskResult> ForgetUserAsync(Guid userId, CancellationToken cancellationToken)
        {
            return Task.FromResult(TaskResult.Succeeded());
        }

        public Task<TaskResult> ForgetDivisionAsync(Guid divisionId, CancellationToken cancellationToken)
        {
            return Task.FromResult(TaskResult.Succeeded());
        }
    }
}
