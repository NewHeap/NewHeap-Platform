using System.Globalization;
using Microsoft.Extensions.AI;

namespace NewHeap.Platform.AI;

/// <summary>
/// File content in model calls. The assistant annotates every file it gives a model with the file's
/// data classification, its estimated tokens and its trust; a governed model call
/// (<see cref="INhAiChatExecutor"/>) then classifies itself as the most sensitive data it carries,
/// counts the file tokens against the profile budget and moves file content that a tool result
/// carries into a user message after the tool results, where providers accept files.
/// </summary>
internal static class NhAiModelFiles
{
    internal const string ClassificationProperty = "newheap.data-classification";
    internal const string EstimatedTokensProperty = "newheap.estimated-tokens";
    internal const string TrustProperty = "newheap.trust";

    /// <summary>
    /// The trust of file content: untrusted external data, as retrieved context is
    /// (<see cref="NhAiContextTrust.UntrustedRetrieved"/>).
    /// </summary>
    internal const string UntrustedRetrieved = "untrusted-retrieved";

    /// <summary>
    /// Marks <paramref name="content"/>, a file or the text a reading tool made of one, as untrusted
    /// external data of <paramref name="classification"/> with an estimated
    /// <paramref name="estimatedTokens"/>.
    /// </summary>
    public static TContent Annotate<TContent>(TContent content, NhAiDataClassification classification, int estimatedTokens)
        where TContent : AIContent
    {
        ArgumentNullException.ThrowIfNull(content);
        content.AdditionalProperties ??= [];
        content.AdditionalProperties[ClassificationProperty] = classification.ToString();
        content.AdditionalProperties[EstimatedTokensProperty] = estimatedTokens.ToString(CultureInfo.InvariantCulture);
        content.AdditionalProperties[TrustProperty] = UntrustedRetrieved;
        return content;
    }

    /// <summary>
    /// The most sensitive classification of the annotated file content in
    /// <paramref name="messages"/>, at least <paramref name="floor"/>.
    /// </summary>
    public static NhAiDataClassification Highest(IEnumerable<ChatMessage> messages, NhAiDataClassification floor)
    {
        var highest = floor;
        foreach (var content in FileContents(messages))
        {
            if (content.AdditionalProperties?.TryGetValue(ClassificationProperty, out var value) == true
                && value is string name
                && Enum.TryParse<NhAiDataClassification>(name, out var classification)
                && Enum.IsDefined(classification)
                && classification > highest)
            {
                highest = classification;
            }
        }

        return highest;
    }

    /// <summary>
    /// The estimated tokens of the annotated file content in <paramref name="messages"/>, as the caller
    /// built them, that the caller's estimate of the text does not hold: all content a tool result
    /// carries, and the files directly in a message. Text a reading tool made of a file directly in a
    /// message is part of the message's text, which <see cref="NhAiChatRequest.EstimatedInputTokens"/>
    /// already counts.
    /// </summary>
    public static long EstimatedTokens(IEnumerable<ChatMessage> messages)
    {
        long total = 0;
        foreach (var message in messages)
        {
            foreach (var content in message.Contents)
            {
                if (content is FunctionResultContent { Result: NhAiToolResultWithContent carried })
                {
                    foreach (var inner in carried.Contents.Where(IsFile))
                    {
                        total += TokensOf(inner);
                    }
                }
                else if (IsFile(content) && content is not TextContent)
                {
                    total += TokensOf(content);
                }
            }
        }

        return total;
    }

    private static int TokensOf(AIContent content)
    {
        return content.AdditionalProperties?.TryGetValue(EstimatedTokensProperty, out var value) == true
            && value is string text
            && int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var tokens)
                ? tokens
                : 0;
    }

    /// <summary>
    /// The messages with the content of every tool result that carries files
    /// (<see cref="NhAiToolResultWithContent"/>) moved into one user message after the tool results of
    /// that round: most providers accept files only in user messages, and every tool result must
    /// follow the call that asked for it. Returns <paramref name="messages"/> itself when no result
    /// carries content.
    /// </summary>
    public static IReadOnlyList<ChatMessage> MoveToolContent(IReadOnlyList<ChatMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);
        if (!messages.Any(message => message.Contents.Any(content => content is FunctionResultContent { Result: NhAiToolResultWithContent })))
        {
            return messages;
        }

        var moved = new List<ChatMessage>(messages.Count + 1);
        var pending = new List<AIContent>();
        foreach (var message in messages)
        {
            if (message.Role != ChatRole.Tool && pending.Count > 0)
            {
                // The round's tool results ended: the files follow them.
                moved.Add(new ChatMessage(ChatRole.User, [.. pending]));
                pending.Clear();
            }
            if (!message.Contents.Any(content => content is FunctionResultContent { Result: NhAiToolResultWithContent }))
            {
                moved.Add(message);
                continue;
            }

            var contents = new List<AIContent>(message.Contents.Count);
            foreach (var content in message.Contents)
            {
                if (content is FunctionResultContent { Result: NhAiToolResultWithContent carried } result)
                {
                    contents.Add(new FunctionResultContent(result.CallId, carried.Result)
                    {
                        Exception = result.Exception
                    });
                    pending.AddRange(carried.Contents);
                    continue;
                }
                contents.Add(content);
            }

            var copy = message.Clone();
            copy.Contents = contents;
            moved.Add(copy);
        }

        if (pending.Count > 0)
        {
            moved.Add(new ChatMessage(ChatRole.User, [.. pending]));
        }

        return moved;
    }

    /// <summary>
    /// The annotated file content of <paramref name="messages"/>, also inside tool results that carry
    /// content.
    /// </summary>
    private static IEnumerable<AIContent> FileContents(IEnumerable<ChatMessage> messages)
    {
        foreach (var message in messages)
        {
            foreach (var content in message.Contents)
            {
                if (content is FunctionResultContent { Result: NhAiToolResultWithContent carried })
                {
                    foreach (var inner in carried.Contents.Where(IsFile))
                    {
                        yield return inner;
                    }
                }
                else if (IsFile(content))
                {
                    yield return content;
                }
            }
        }
    }

    private static bool IsFile(AIContent content)
    {
        return content.AdditionalProperties?.ContainsKey(ClassificationProperty) == true;
    }
}

/// <summary>
/// A tool result that carries content the model reads after the tool results of its round, such as
/// the files a tool returned: <paramref name="Result"/> is what the model reads as the tool's result,
/// <paramref name="Contents"/> the external data that follows in a user message.
/// </summary>
internal sealed record NhAiToolResultWithContent(object? Result, IReadOnlyList<AIContent> Contents);
