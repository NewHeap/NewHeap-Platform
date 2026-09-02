using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace NewHeap.Platform.AI.Test;

public sealed record NhAiRecordedChatRequest(
    IReadOnlyList<ChatMessage> Messages,
    ChatOptions? Options);

public sealed class NhAiDeterministicChatClient : IChatClient
{
    private readonly object _sync = new();
    private readonly Queue<ChatResponse> _responses = [];
    private readonly List<NhAiRecordedChatRequest> _requests = [];

    public NhAiDeterministicChatClient(params string[] responses)
    {
        ArgumentNullException.ThrowIfNull(responses);
        foreach (var response in responses)
        {
            EnqueueResponse(response);
        }
    }

    public IReadOnlyList<NhAiRecordedChatRequest> Requests
    {
        get
        {
            lock (_sync)
            {
                return _requests.ToArray();
            }
        }
    }

    public void EnqueueResponse(string response)
    {
        ArgumentNullException.ThrowIfNull(response);
        EnqueueResponse(new ChatResponse(new ChatMessage(ChatRole.Assistant, response)));
    }

    public void EnqueueResponse(ChatResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        lock (_sync)
        {
            _responses.Enqueue(response);
        }
    }

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(DequeueResponse(messages, options));
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);
        cancellationToken.ThrowIfCancellationRequested();
        var response = DequeueResponse(messages, options);
        foreach (var update in response.ToChatResponseUpdates())
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            yield return update;
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        return serviceKey is null && serviceType.IsInstanceOfType(this)
            ? this
            : null;
    }

    public void Dispose()
    {
    }

    private ChatResponse DequeueResponse(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options)
    {
        lock (_sync)
        {
            _requests.Add(new NhAiRecordedChatRequest(messages.ToArray(), options));
            return _responses.Count > 0
                ? _responses.Dequeue()
                : throw new InvalidOperationException(
                    "No deterministic chat response is queued.");
        }
    }
}

public sealed class NhAiDeterministicEmbeddingGenerator :
    IEmbeddingGenerator<string, Embedding<float>>
{
    private readonly Func<string, ReadOnlyMemory<float>> _createVector;
    private readonly object _sync = new();
    private readonly List<string> _inputs = [];

    public NhAiDeterministicEmbeddingGenerator(
        Func<string, ReadOnlyMemory<float>>? createVector = null)
    {
        _createVector = createVector ?? CreateDefaultVector;
    }

    public IReadOnlyList<string> Inputs
    {
        get
        {
            lock (_sync)
            {
                return _inputs.ToArray();
            }
        }
    }

    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values,
        EmbeddingGenerationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(values);
        cancellationToken.ThrowIfCancellationRequested();
        var inputs = values.ToArray();
        lock (_sync)
        {
            _inputs.AddRange(inputs);
        }
        var embeddings = inputs
            .Select(value => new Embedding<float>(_createVector(value)))
            .ToArray();
        return Task.FromResult(
            new GeneratedEmbeddings<Embedding<float>>(embeddings));
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        return serviceKey is null && serviceType.IsInstanceOfType(this)
            ? this
            : null;
    }

    public void Dispose()
    {
    }

    private static ReadOnlyMemory<float> CreateDefaultVector(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var sum = value.Aggregate(0, (current, character) => current + character);
        return new float[]
        {
            value.Length,
            sum,
            value.Length == 0 ? 0 : value[0]
        };
    }
}
