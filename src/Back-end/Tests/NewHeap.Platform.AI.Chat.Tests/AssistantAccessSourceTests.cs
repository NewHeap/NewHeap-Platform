using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NewHeap.Platform.AI.AspNet;
using NewHeap.Platform.AI.Chat.AspNet.Mcp;
using NewHeap.Platform.AspNet.Common.Services;
using NSubstitute;
using Xunit;

namespace NewHeap.Platform.AI.Chat.Tests;

/// <summary>
/// The assistant with principals from the application's access source (S6). A missing or stale
/// context says that the application holds no fresh rights of a person, not that the person was removed,
/// so the MCP owner sweep does not remove connections on it; their retention still applies.
/// </summary>
public sealed class AssistantAccessSourceTests
{
    [Fact]
    public async Task The_mcp_owner_sweep_removes_no_connection_on_the_access_sources_answer()
    {
        var source = new CountingSource();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAuthorizationCore();
        // The NewHeap user store is there too, so the sweep would ask a principal factory that reads it.
        services.AddSingleton(Substitute.For<INhUserManager>());
        services.AddSingleton(source);
        services.AddNewHeapPlatformAIAspNet(ai =>
            ai.UsePrincipalsFromAccessSource<CountingSource>(options => options.MaxAge = TimeSpan.FromMinutes(30)));
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();

        // A sweep that does not ask the principal factory reads no owner, so it needs none of its stores.
        var sweep = new NhAssistantMcpOwnerSweep(null!, null!, null!, scope.ServiceProvider, NullLogger<NhAssistantMcpOwnerSweep>.Instance);
        var batch = await sweep.RunBatchAsync(null, 100, CancellationToken.None);

        Assert.Equal(new NhAssistantMcpOwnerSweepBatch(0, null, 0), batch);
        Assert.Equal(0, source.Asked);
    }

    private sealed class CountingSource : INhAiAccessSource
    {
        private int _asked;

        public int Asked => Volatile.Read(ref _asked);

        public ValueTask<NhAiAccessContext?> GetAsync(
            string actorId,
            NhAiSubjectInfo subject,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _asked);
            return ValueTask.FromResult<NhAiAccessContext?>(null);
        }
    }
}
