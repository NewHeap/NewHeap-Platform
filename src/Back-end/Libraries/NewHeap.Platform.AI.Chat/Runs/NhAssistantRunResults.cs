using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.AI.Chat.Runtime;

namespace NewHeap.Platform.AI.Chat.Runs;

/// <summary>
/// The result of a run as people read it (K13): its full answer as Markdown, at most
/// <see cref="NhAssistantLimits.MaxRunResultCharacters"/> characters, next to the bounded summary
/// <see cref="NhAssistantRunSummaries"/> writes for the agent's memory. The answer is model output and
/// stays untrusted text; screens render it with the assistant's safe Markdown renderer only. When the
/// run's rules wrote their answer per culture, the run also keeps its result in each supported UI
/// culture (<see cref="AssistantRun.ResultsJson"/>) and readers get it in their own culture.
/// </summary>
internal static class NhAssistantRunResults
{
    /// <summary>
    /// The most characters of a result a listing shows.
    /// </summary>
    public const int MaxPreviewCharacters = 300;

    /// <summary>
    /// The longest results per culture as stored JSON: every culture an approval is presented in, each
    /// with a whole result that JSON may escape.
    /// </summary>
    public const int MaxLocalizedSerializedLength =
        NhAssistantToolPresentationResolver.MaxLocalizedCultures * (NhAssistantLimits.MaxRunResultCharacters * 6 + 128);

    /// <summary>
    /// Separates the answers of a run's turns: a Markdown thematic break between paragraphs.
    /// </summary>
    internal const string TurnSeparator = "\n\n---\n\n";

    /// <summary>
    /// The first <see cref="MaxPreviewCharacters"/> characters of the run's result for a reader in
    /// <paramref name="culture"/> (see <see cref="ResultOf"/>), never cutting a character in two, and
    /// whether there is more; <see langword="null"/> while the run has none. A run without a stored
    /// answer, such as one that ended before runs kept their answer or one that failed before it
    /// answered, shows the summary it ended with.
    /// </summary>
    public static (string? Text, bool Truncated) PreviewOf(AssistantRun run, CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(run);
        var (stored, _) = ResultOf(run, culture);
        var result = string.IsNullOrWhiteSpace(stored) ? run.Summary : stored;
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
    /// The run's result for a reader in <paramref name="culture"/> and whether it was cut. When the run
    /// keeps its result per culture: the exact culture, then its parents, then another culture of the
    /// same language, as proposals and stale explanations are picked; otherwise, and for a model's
    /// answer, the result in the run's own culture.
    /// </summary>
    public static (string? Text, bool Truncated) ResultOf(AssistantRun run, CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(culture);
        if (DeserializeLocalized(run.ResultsJson) is { Count: > 0 } localized)
        {
            for (var candidate = culture; !string.IsNullOrEmpty(candidate.Name); candidate = candidate.Parent)
            {
                if (localized.TryGetValue(candidate.Name, out var exact))
                {
                    return (exact.Text, exact.Truncated);
                }
            }

            foreach (var (name, result) in localized)
            {
                if (SameLanguage(name, culture))
                {
                    return (result.Text, result.Truncated);
                }
            }
        }

        return (run.Result, run.ResultTruncated);
    }

    /// <summary>
    /// The results per culture after <paramref name="answer"/> of one more turn, as stored JSON, or
    /// <paramref name="resultsJson"/> unchanged while the run keeps its result in one language. A turn
    /// that answered per culture (<paramref name="localizedAnswer"/>) starts them; a culture without a
    /// result of its own yet starts from the result so far (<paramref name="result"/>). Every culture
    /// takes its own text of the turn, or <paramref name="answer"/> when the turn has none in that
    /// culture, such as a model's answer, and is bounded like the result.
    /// </summary>
    public static string? AppendLocalized(
        string? resultsJson,
        string? result,
        bool truncated,
        string answer,
        IReadOnlyDictionary<string, string>? localizedAnswer)
    {
        ArgumentNullException.ThrowIfNull(answer);
        var existing = DeserializeLocalized(resultsJson);
        if (existing is not { Count: > 0 } && localizedAnswer is not { Count: > 0 })
        {
            return resultsJson;
        }

        var cultures = (existing?.Keys ?? Enumerable.Empty<string>())
            .Concat(localizedAnswer?.Keys ?? Enumerable.Empty<string>())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(NhAssistantToolPresentationResolver.MaxLocalizedCultures)
            .ToArray();
        var appended = new Dictionary<string, NhAssistantLocalizedRunResult>(StringComparer.OrdinalIgnoreCase);
        foreach (var culture in cultures)
        {
            var before = existing is not null && existing.TryGetValue(culture, out var kept)
                ? kept
                : new NhAssistantLocalizedRunResult(result ?? string.Empty, truncated);
            var text = localizedAnswer is not null && localizedAnswer.TryGetValue(culture, out var own)
                ? own
                : answer;
            var (combined, cut) = Append(before.Text, before.Truncated, text);
            appended[culture] = new NhAssistantLocalizedRunResult(combined ?? string.Empty, cut);
        }

        return JsonSerializer.Serialize(appended, NhAssistantContent.JsonOptions);
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

    private static Dictionary<string, NhAssistantLocalizedRunResult>? DeserializeLocalized(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        Dictionary<string, NhAssistantLocalizedRunResult>? localized;
        try
        {
            localized = JsonSerializer.Deserialize<Dictionary<string, NhAssistantLocalizedRunResult>>(
                json,
                NhAssistantContent.JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }

        if (localized is null)
        {
            return null;
        }

        return new Dictionary<string, NhAssistantLocalizedRunResult>(
            localized.Where(entry => entry.Value is { Text: not null }),
            StringComparer.OrdinalIgnoreCase);
    }

    private static bool SameLanguage(string cultureName, CultureInfo culture)
    {
        try
        {
            return string.Equals(
                CultureInfo.GetCultureInfo(cultureName).TwoLetterISOLanguageName,
                culture.TwoLetterISOLanguageName,
                StringComparison.OrdinalIgnoreCase);
        }
        catch (CultureNotFoundException)
        {
            return false;
        }
    }
}

/// <summary>
/// A run's result in one culture, as <see cref="AssistantRun.ResultsJson"/> keeps it.
/// </summary>
/// <param name="Text">The result in that culture.</param>
/// <param name="Truncated">The result was longer than <see cref="NhAssistantLimits.MaxRunResultCharacters"/> characters.</param>
internal sealed record NhAssistantLocalizedRunResult(string Text, bool Truncated);

/// <summary>
/// Writes a text of a run in the culture the run works in and in every supported UI culture, with
/// each culture current while it is written, for a result people read in their own language.
/// </summary>
internal sealed class NhAssistantRunResultCultures(
    ILogger<NhAssistantRunResultCultures> logger,
    INhAssistantPresentationCultures? presentationCultures = null)
{
    /// <summary>
    /// The text in the current UI culture and each supported UI culture, by culture name, the current
    /// culture first; <see langword="null"/> when the application supports no other culture. A culture
    /// whose text fails or is empty is left out, so readers in it get the run's own culture.
    /// </summary>
    public IReadOnlyDictionary<string, string>? Render(Func<CultureInfo, string?> write)
    {
        ArgumentNullException.ThrowIfNull(write);
        var cultures = new[] { CultureInfo.CurrentUICulture }
            .Concat(presentationCultures?.GetCultures() ?? [])
            .Where(culture => !string.IsNullOrEmpty(culture.Name))
            .DistinctBy(culture => culture.Name, StringComparer.OrdinalIgnoreCase)
            .Take(NhAssistantToolPresentationResolver.MaxLocalizedCultures)
            .ToArray();
        if (cultures.Length < 2)
        {
            return null;
        }

        var texts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var culture in cultures)
        {
            if (WriteIn(write, culture) is { } text)
            {
                texts[culture.Name] = text;
            }
        }

        return texts.Count > 0 ? texts : null;
    }

    private string? WriteIn(Func<CultureInfo, string?> write, CultureInfo culture)
    {
        // Rule handlers read their texts through the current culture; the change stays inside this call.
        var (previousCulture, previousUiCulture) = (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture);
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;
        try
        {
            var text = write(culture)?.Trim();
            return string.IsNullOrEmpty(text) ? null : text;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Application rules can carry content in exception messages; log the type only.
            logger.LogWarning(
                "A run's answer could not be written in culture {Culture} ({ExceptionType}).",
                culture.Name,
                exception.GetType().Name);
            return null;
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
            CultureInfo.CurrentUICulture = previousUiCulture;
        }
    }
}

/// <summary>
/// Keeps the answer of each turn of a run that completed as the run's result: the text of the turn's
/// assistant messages, the rules' summary of a rule turn included, as the thread shows it, and, when
/// the rules wrote it per culture, the turn's answer in each culture.
/// </summary>
internal sealed class NhAssistantRunResultWriter(NhAssistantRunStore runs)
{
    /// <summary>
    /// Appends the answer of <paramref name="turnId"/> to the run's result, and
    /// <paramref name="localizedAnswer"/>, the same answer per culture, to its results per culture. A
    /// turn without text adds nothing. A run's turns run one after another in its own operation, so
    /// nothing else writes the result meanwhile.
    /// </summary>
    public async Task AppendTurnAsync(
        Guid runId,
        Guid conversationId,
        Guid turnId,
        IReadOnlyDictionary<string, string>? localizedAnswer,
        CancellationToken cancellationToken)
    {
        var parts = await runs.GetTurnAnswerPartsAsync(conversationId, turnId, cancellationToken);
        var answer = string.Join(
            "\n\n",
            parts
                .Select(json => NhAssistantContent.TextOf(NhAssistantContent.DeserializeParts(json)).Trim())
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
        var resultsJson = NhAssistantRunResults.AppendLocalized(
            run.ResultsJson,
            run.Result,
            run.ResultTruncated,
            answer,
            localizedAnswer);
        if (truncated == run.ResultTruncated
            && string.Equals(result, run.Result, StringComparison.Ordinal)
            && string.Equals(resultsJson, run.ResultsJson, StringComparison.Ordinal))
        {
            return;
        }

        await runs.SetRunResultAsync(runId, result, truncated, resultsJson, cancellationToken);
    }
}
