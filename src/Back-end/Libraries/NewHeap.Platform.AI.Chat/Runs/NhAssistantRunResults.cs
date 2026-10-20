using NewHeap.Platform.AI.Chat.Entities;

namespace NewHeap.Platform.AI.Chat.Runs;

/// <summary>
/// The result of a run as people read it in a listing, such as the latest result of each agent on
/// an object's page. Runs keep no result of their own yet: until they store their full answer next to
/// their summary (K13, phase W6 of the plan for agents working on application objects), the result is
/// the bounded summary <see cref="NhAssistantRunSummaries"/> writes for the agent's memory. W6 reads its
/// stored result here first, so every listing changes in one place.
/// </summary>
internal static class NhAssistantRunResults
{
    /// <summary>
    /// The most characters of a result a listing shows.
    /// </summary>
    public const int MaxPreviewCharacters = 300;

    /// <summary>
    /// The first <see cref="MaxPreviewCharacters"/> characters of the run's result, never cutting a
    /// character in two, and whether the result is longer; <see langword="null"/> while the run has
    /// none.
    /// </summary>
    public static (string? Text, bool Truncated) PreviewOf(AssistantRun run)
    {
        ArgumentNullException.ThrowIfNull(run);
        var result = run.Summary;
        if (string.IsNullOrWhiteSpace(result))
        {
            return (null, false);
        }
        if (result.Length <= MaxPreviewCharacters)
        {
            return (result, false);
        }

        var length = char.IsHighSurrogate(result[MaxPreviewCharacters - 1]) ? MaxPreviewCharacters - 1 : MaxPreviewCharacters;
        return (result[..length], true);
    }
}
