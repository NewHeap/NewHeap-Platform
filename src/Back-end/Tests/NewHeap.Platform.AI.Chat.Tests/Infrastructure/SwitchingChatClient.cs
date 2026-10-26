using Microsoft.Extensions.AI;

namespace NewHeap.Platform.AI.Chat.Tests.Infrastructure;

/// <summary>
/// The model of a test host, which a test replaces between turns: a scripted model expects the result of
/// every call it made in the next request, while a turn after a closed wait, or a turn of another
/// conversation, reads the chat's history instead, so it gets a script of its own.
/// </summary>
internal sealed class SwitchingChatClient(IChatClient initial) : IChatClient
{
    public IChatClient Current { get; set; } = initial;

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        return Current.GetResponseAsync(messages, options, cancellationToken);
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        return Current.GetStreamingResponseAsync(messages, options, cancellationToken);
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        return serviceKey is null && serviceType.IsInstanceOfType(this) ? this : Current.GetService(serviceType, serviceKey);
    }

    public void Dispose()
    {
    }
}
