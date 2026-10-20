using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Chat.Persistence;

namespace NewHeap.Platform.AI.Chat.Runs;

/// <summary>
/// The result of a run as people read it (K13): its full answer as Markdown, at most
/// <see cref="NhAssistantLimits.MaxRunResultCharacters"/> characters, next to the bounded summary
/// <see cref="NhAssistantRunSummaries"/> writes for the agent's memory. The answer is model output and
/// stays untrusted text; screens render it with the assistant's safe Markdown renderer only.
/// </summary>
internal static class NhAssistantRunResults
{
    /// <summary>
    /// The most characters of a result a listing shows.
    /// </summary>
    public const int MaxPreviewCharacters = 300;

    /// <summary>
    /// Separates the answers of a run's turns: a Markdown thematic break between paragraphs.
    /// </summary>
    internal const string TurnSeparator = "\n\n---\n\n";

    /// <summary>
    /// The first <see cref="MaxPreviewCharacters"/> characters of the run's result, never cutting a
    /// character in two, and whether there is more; <see langword="null"/> while the run has none. A
    /// run without a stored answer, such as one that ended before runs kept their answer or one that
    /// failed before it answered, shows the summary it ended with.
    /// </summary>
    public static (string? Text, bool Truncated) PreviewOf(AssistantRun run)
    {
        ArgumentNullException.ThrowIfNull(run);
        var result = string.IsNullOrWhiteSpace(run.Result) ? run.Summary : run.Result;
        if (string.IsNullOrWhiteSpace(result))
        {
            return (null, false);
        }
        if (result.Length <= MaxPreviewCharacters)
        {
            return (result, false);
        }

        return (Cut(result, MaxPreviewCharacters), true);
    }

    /// <summary>
    /// The result after <paramref name="answer"/> of one more turn: appended to what the run kept,
    /// separated by <see cref="TurnSeparator"/>, and bounded to
    /// <see cref="NhAssistantLimits.MaxRunResultCharacters"/>. A result that was cut already stays as
    /// it is.
    /// </summary>
    public static (string? Result, bool Truncated) Append(string? existing, bool truncated, string answer)
    {
        ArgumentNullException.ThrowIfNull(answer);
        if (truncated)
        {
            return (existing, true);
        }

        var text = answer.Trim();
        if (text.Length == 0)
        {
            return (existing, false);
        }

        var combined = string.IsNullOrEmpty(existing) ? text : existing + TurnSeparator + text;
        return combined.Length <= NhAssistantLimits.MaxRunResultCharacters
            ? (combined, false)
            : (Cut(combined, NhAssistantLimits.MaxRunResultCharacters), true);
    }

    /// <summary>
    /// The first <paramref name="maximum"/> characters, one less when the last would be the first half
    /// of a surrogate pair.
    /// </summary>
    private static string Cut(string text, int maximum)
    {
        var length = char.IsHighSurrogate(text[maximum - 1]) ? maximum - 1 : maximum;
        return text[..length];
    }
}

/// <summary>
/// Keeps the answer of each turn of a run that completed as the run's result: the text of the turn's
/// assistant messages, the rules' summary of a rule turn included, as the thread shows it.
/// </summary>
internal sealed class NhAssistantRunResultWriter(NhAssistantRunStore runs)
{
    /// <summary>
    /// Appends the answer of <paramref name="turnId"/> to the run's result. A turn without text adds
    /// nothing. A run's turns run one after another in its own operation, so nothing else writes the
    /// result meanwhile.
    /// </summary>
    public async Task AppendTurnAsync(
        Guid runId,
        Guid conversationId,
        Guid turnId,
        CancellationToken cancellationToken)
    {
        var parts = await runs.GetTurnAnswerPartsAsync(conversationId, turnId, cancellationToken);
        var answer = string.Join(
            "\n\n",
            parts
                .Select(json => Runtime.NhAssistantContent.TextOf(Runtime.NhAssistantContent.DeserializeParts(json)).Trim())
                .Where(text => text.Length > 0));
        if (answer.Length == 0)
        {
            return;
        }

        var run = await runs.FindRunAsync(runId, cancellationToken);
        if (run is null)
        {
            return;
        }

        var (result, truncated) = NhAssistantRunResults.Append(run.Result, run.ResultTruncated, answer);
        if (truncated == run.ResultTruncated && string.Equals(result, run.Result, StringComparison.Ordinal))
        {
            return;
        }

        await runs.SetRunResultAsync(runId, result, truncated, cancellationToken);
    }
}
