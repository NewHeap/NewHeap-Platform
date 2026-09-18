using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace NewHeap.Platform.AI.Test;

/// <summary>
/// A deterministic <see cref="IChatClient"/> that plays a script of model responses: text,
/// function calls, or both. Every response that follows a function call first verifies that the
/// incoming messages contain a <see cref="FunctionResultContent"/> for each call it issued, so a
/// test proves the tool result actually reached the model. Streaming splits text into word chunks
/// and ends with usage content; non-streaming returns the same response in one piece.
/// </summary>
public sealed class NhAiScriptedChatClient : IChatClient
{
    private static readonly JsonSerializerOptions ArgumentOptions = new(JsonSerializerDefaults.Web);

    private readonly object _sync = new();
    private readonly List<ScriptStep> _steps = [];
    private readonly List<NhAiRecordedChatRequest> _requests = [];
    private readonly List<FunctionResultContent> _functionResults = [];
    private readonly HashSet<string> _pendingCallIds = new(StringComparer.Ordinal);
    private readonly bool _loop;
    private int _position;
    private int _generatedCallIds;

    /// <param name="loop">Restart the script when it is exhausted instead of failing.</param>
    public NhAiScriptedChatClient(bool loop = false)
    {
        _loop = loop;
    }

    /// <summary>
    /// Every request the client received, in order.
    /// </summary>
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

    /// <summary>
    /// Every function result the client verified, in order.
    /// </summary>
    public IReadOnlyList<FunctionResultContent> FunctionResults
    {
        get
        {
            lock (_sync)
            {
                return _functionResults.ToArray();
            }
        }
    }

    /// <summary>
    /// The number of scripted responses that have not been played yet.
    /// </summary>
    public int RemainingSteps
    {
        get
        {
            lock (_sync)
            {
                return _steps.Count - _position;
            }
        }
    }

    /// <summary>
    /// Adds a response that contains only text.
    /// </summary>
    public NhAiScriptedChatClient RespondWithText(string text)
    {
        ArgumentException.ThrowIfNullOrEmpty(text);
        return Add(new ScriptStep(text, []));
    }

    /// <summary>
    /// Adds a response that calls one function. <paramref name="arguments"/> is serialized with web
    /// defaults and its top-level properties become the call arguments, for example
    /// <c>new { input = new { query = "roadmap" } }</c> for a generated NewHeap tool.
    /// </summary>
    public NhAiScriptedChatClient RespondWithFunctionCall(
        string functionName,
        object arguments,
        string? callId = null,
        string? text = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(functionName);
        ArgumentNullException.ThrowIfNull(arguments);
        var element = JsonSerializer.SerializeToElement(arguments, arguments.GetType(), ArgumentOptions);
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("Scripted function arguments must serialize to a JSON object.", nameof(arguments));
        }

        var dictionary = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            dictionary[property.Name] = property.Value.Clone();
        }
        return Add(new ScriptStep(text, [new ScriptedCall(functionName, dictionary, callId)]));
    }

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);
        cancellationToken.ThrowIfCancellationRequested();
        var played = Play(messages, options);
        return Task.FromResult(new ChatResponse(played.Message)
        {
            ResponseId = played.ResponseId,
            FinishReason = played.FinishReason,
            Usage = played.Usage
        });
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);
        cancellationToken.ThrowIfCancellationRequested();
        var played = Play(messages, options);
        foreach (var content in played.Message.Contents)
        {
            if (content is TextContent textContent)
            {
                foreach (var chunk in SplitIntoChunks(textContent.Text))
                {
                    await Task.Yield();
                    cancellationToken.ThrowIfCancellationRequested();
                    yield return Update(played, new TextContent(chunk));
                }
            }
            else
            {
                await Task.Yield();
                cancellationToken.ThrowIfCancellationRequested();
                yield return Update(played, content);
            }
        }

        var final = Update(played, new UsageContent(played.Usage));
        final.FinishReason = played.FinishReason;
        yield return final;
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

    private NhAiScriptedChatClient Add(ScriptStep step)
    {
        lock (_sync)
        {
            _steps.Add(step);
        }
        return this;
    }

    private PlayedResponse Play(IEnumerable<ChatMessage> messages, ChatOptions? options)
    {
        var received = messages.ToArray();
        lock (_sync)
        {
            _requests.Add(new NhAiRecordedChatRequest(received, options));
            VerifyPendingFunctionResults(received);
            if (_position >= _steps.Count)
            {
                if (!_loop || _steps.Count == 0)
                {
                    throw new InvalidOperationException("The scripted chat client has no response left.");
                }
                _position = 0;
            }

            var step = _steps[_position++];
            var contents = new List<AIContent>();
            if (!string.IsNullOrEmpty(step.Text))
            {
                contents.Add(new TextContent(step.Text));
            }
            foreach (var call in step.Calls)
            {
                var callId = call.CallId ?? $"scripted-call-{++_generatedCallIds}";
                _pendingCallIds.Add(callId);
                contents.Add(new FunctionCallContent(callId, call.FunctionName, new Dictionary<string, object?>(call.Arguments)));
            }

            var inputCharacters = received.Sum(message => (long)message.Text.Length)
                + (options?.Instructions?.Length ?? 0);
            var outputCharacters = step.Text?.Length ?? 0;
            var usage = new UsageDetails
            {
                InputTokenCount = Math.Max(1, (inputCharacters + 3) / 4),
                OutputTokenCount = Math.Max(1, (outputCharacters + 3) / 4),
            };
            usage.TotalTokenCount = usage.InputTokenCount + usage.OutputTokenCount;
            return new PlayedResponse(
                new ChatMessage(ChatRole.Assistant, contents) { MessageId = Guid.NewGuid().ToString("N") },
                "scripted-" + _requests.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                step.Calls.Count > 0 ? ChatFinishReason.ToolCalls : ChatFinishReason.Stop,
                usage);
        }
    }

    private void VerifyPendingFunctionResults(IReadOnlyList<ChatMessage> received)
    {
        if (_pendingCallIds.Count == 0)
        {
            return;
        }

        var results = received
            .SelectMany(message => message.Contents.OfType<FunctionResultContent>())
            .Where(result => _pendingCallIds.Contains(result.CallId))
            .ToArray();
        var missing = _pendingCallIds
            .Where(callId => results.All(result => !string.Equals(result.CallId, callId, StringComparison.Ordinal)))
            .ToArray();
        if (missing.Length > 0)
        {
            throw new InvalidOperationException(
                $"The scripted chat client expected a function result for call '{missing[0]}'.");
        }

        _functionResults.AddRange(results);
        _pendingCallIds.Clear();
    }

    private static ChatResponseUpdate Update(PlayedResponse played, AIContent content)
    {
        return new ChatResponseUpdate(ChatRole.Assistant, [content])
        {
            MessageId = played.Message.MessageId,
            ResponseId = played.ResponseId
        };
    }

    private static IEnumerable<string> SplitIntoChunks(string text)
    {
        var start = 0;
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] == ' ' && index > start)
            {
                yield return text[start..(index + 1)];
                start = index + 1;
            }
        }
        if (start < text.Length)
        {
            yield return text[start..];
        }
    }

    private sealed record ScriptedCall(
        string FunctionName,
        IReadOnlyDictionary<string, object?> Arguments,
        string? CallId);

    private sealed record ScriptStep(
        string? Text,
        IReadOnlyList<ScriptedCall> Calls);

    private sealed record PlayedResponse(
        ChatMessage Message,
        string ResponseId,
        ChatFinishReason FinishReason,
        UsageDetails Usage);
}
