using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using NewHeap.Platform.AI.Chat.Runtime;
using NewHeap.Platform.AI.Chat.Tests.Infrastructure;
using Xunit;

namespace NewHeap.Platform.AI.Chat.Tests;

/// <summary>
/// A governed model call that ends at its deadline is the failed result <c>ai-model-timeout</c>; the
/// agent's chat client throws it with that code, and the turn ends as it did when the deadline threw:
/// failed with <see cref="NhAssistantErrorCodes.TurnTimeout"/> (LS39).
/// </summary>
public sealed class AssistantModelTimeoutTests(AssistantDatabaseFixture database) : IClassFixture<AssistantDatabaseFixture>
{
    [Fact]
    public async Task A_model_call_that_times_out_fails_the_turn_with_the_turn_timeout_code()
    {
        // The provider client's own timeout cancels the call while the turn still waits for it.
        var model = new TimingOutChatClient();
        await using var host = await AssistantTestHost.CreateAsync(database, AssistantTestProvider.PostgreSql, model);
        var conversation = await host.CreateConversationAsync();

        var turn = await host.SendAsync(conversation.Id, "Think for a long time.");

        var end = Assert.IsType<NhAssistantTurnCompletedEvent>(turn.Events[^1]);
        Assert.Equal(NhAssistantTurnStatuses.Failed, end.Status);
        Assert.Equal(NhAssistantErrorCodes.TurnTimeout, end.ErrorCode);
        var usage = Assert.Single(host.Usage.Records);
        Assert.Equal(NhAiOutcomeKind.TimedOut, usage.Outcome);
        Assert.Equal(NhAiModelFinishReasons.TimedOut, usage.FinishReason);
        Assert.Contains(host.Logs.Messages, message => message.Contains("ended at the deadline of a model call", StringComparison.Ordinal));
        Assert.Equal(NhAssistantConversationStatuses.Failed, (await host.ReloadAsync(conversation.Id)).Status);
    }

    /// <summary>
    /// A model whose client gives up as an HTTP client's timeout does: a cancellation that its caller
    /// did not request.
    /// </summary>
    private sealed class TimingOutChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            throw new TaskCanceledException("The provider request timed out.", new TimeoutException());
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            throw new TaskCanceledException("The provider request timed out.", new TimeoutException());
#pragma warning disable CS0162
            yield break;
#pragma warning restore CS0162
        }

        public object? GetService(Type serviceType, object? serviceKey = null)
        {
            return null;
        }

        public void Dispose()
        {
        }
    }
}
