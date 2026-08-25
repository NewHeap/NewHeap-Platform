using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace SampleProjectManagement.Core.Services;

public sealed class ProjectAiSampleChatClient : IChatClient
{
    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(
            new ChatResponse(
                new ChatMessage(
                    ChatRole.Assistant,
                    "The local sample model completed the governed project request."))
            {
                FinishReason = ChatFinishReason.Stop,
                ModelId = "sample-project-model"
            });
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await Task.Yield();
        yield return new ChatResponseUpdate(
            ChatRole.Assistant,
            "The local sample model completed the governed project request.")
        {
            FinishReason = ChatFinishReason.Stop,
            ModelId = "sample-project-model"
        };
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        return serviceKey is null && serviceType.IsInstanceOfType(this)
            ? this
            : null;
    }

    public void Dispose()
    {
    }
}
