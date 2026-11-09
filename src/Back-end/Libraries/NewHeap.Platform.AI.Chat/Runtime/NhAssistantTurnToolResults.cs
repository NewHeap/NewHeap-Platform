using System.Text.Encodings.Web;
using System.Text.Json;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.AI.Chat.Runtime;
using NewHeap.Platform.Common.Models;

namespace NewHeap.Platform.AI.Chat;

/// <summary>
/// Tells application code whether a value appears in what a tool returned earlier in the turn that is
/// running now, so a proposal guard or a tool can refuse a value the model made up. It compares and
/// discloses nothing: the caller learns which calls hold the value, never what they returned. It answers
/// only inside an assistant turn, which is where <see cref="NhAiProposalGuardStages.Proposal"/> runs;
/// at the later stages no turn is running and the call fails with
/// <see cref="NhAssistantToolResultCodes.NoTurn"/>.
/// </summary>
public interface INhAssistantTurnToolResults
{
    /// <summary>
    /// Searches the results of the calls of this turn that ended successfully, also those before a
    /// question or decision the turn waited for. A result is searched by its values and property names,
    /// after JSON decoding, so a value with quotes or accents matches as the tool returned it. The
    /// assistant's own functions (skills, questions, memory, consultations) and the script tool are no
    /// source: what they return was written by a model.
    /// </summary>
    /// <param name="value">The value to find, literally; a part of a longer text counts.</param>
    /// <param name="ignoreCase">Compares without regard to case, for values such as mail addresses.</param>
    /// <param name="cancellationToken">Cancels the search.</param>
    Task<TaskResult<NhAssistantToolResultSearch>> FindAsync(
        string value,
        bool ignoreCase = false,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// What a search of the turn's tool results found.
/// </summary>
/// <param name="Matches">The calls whose results hold the value, oldest first.</param>
/// <param name="SearchedCalls">The calls that could be a source.</param>
/// <param name="CutShortCalls">The searched calls whose result was cut at
/// <see cref="NhAssistantLimits.MaxStoredJsonCharacters"/>, so a value past that point is not seen.</param>
public sealed record NhAssistantToolResultSearch(
    IReadOnlyList<NhAssistantToolResultMatch> Matches,
    int SearchedCalls,
    int CutShortCalls)
{
    public bool Found => Matches.Count > 0;

    /// <summary>
    /// Whether every source was searched whole, so <see cref="Found"/> being false means the value is
    /// not there. With a result cut short the answer is "not seen", which a guard should not refuse on.
    /// </summary>
    public bool Complete => CutShortCalls == 0;
}

/// <summary>
/// One call whose result holds the searched value.
/// </summary>
/// <param name="InvocationId">The call.</param>
/// <param name="ToolId">Its tool, so a guard can require the tool a value should come from.</param>
/// <param name="InArguments">Whether the model passed the value to the call itself. A lookup that
/// returns what it was asked confirms the value exists; a search that merely repeats its query in its
/// result proves nothing, so a guard decides per tool.</param>
public sealed record NhAssistantToolResultMatch(
    Guid InvocationId,
    string ToolId,
    bool InArguments);

/// <summary>
/// Stable, content-free failure codes of <see cref="INhAssistantTurnToolResults"/>.
/// </summary>
public static class NhAssistantToolResultCodes
{
    /// <summary>
    /// No assistant turn is running here, as at revalidation, decision and execution of a proposal.
    /// </summary>
    public const string NoTurn = "assistant-tool-results-no-turn";

    /// <summary>
    /// The turn keeps no record of its calls: a consultation stores nothing in the thread it answers.
    /// </summary>
    public const string NotRecorded = "assistant-tool-results-not-recorded";
}

internal sealed class NhAssistantTurnToolResults(
    INhAssistantStore store,
    IEnumerable<INhAiToolCatalog> catalogs) : INhAssistantTurnToolResults
{
    private const string AssistantFunctionPrefix = "assistant.";

    public async Task<TaskResult<NhAssistantToolResultSearch>> FindAsync(
        string value,
        bool ignoreCase = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (NhAssistantExecutionScope.CurrentTurn is not { } turn)
        {
            return TaskResult<NhAssistantToolResultSearch>.Failed(
                NhAssistantToolResultCodes.NoTurn,
                "No assistant turn is running.");
        }
        if (turn.Consultation is not null)
        {
            return TaskResult<NhAssistantToolResultSearch>.Failed(
                NhAssistantToolResultCodes.NotRecorded,
                "A consultation keeps no record of its tool calls.");
        }

        var comparison = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var calls = await store.GetTurnToolResultsAsync(turn.ConversationId, turn.TurnId, cancellationToken);
        return TaskResult<NhAssistantToolResultSearch>.Succeeded(Search(calls, value, comparison, ScriptToolIds()));
    }

    /// <summary>
    /// Searches <paramref name="calls"/> for <paramref name="value"/>.
    /// </summary>
    internal static NhAssistantToolResultSearch Search(
        IEnumerable<NhAssistantTurnToolResult> calls,
        string value,
        StringComparison comparison,
        IReadOnlySet<string> scriptTools)
    {
        var matches = new List<NhAssistantToolResultMatch>();
        var searched = 0;
        var cutShort = 0;
        foreach (var call in calls.Where(call => IsSource(call.ToolId, scriptTools)))
        {
            searched++;
            if (call.ResultJson is { Length: >= NhAssistantLimits.MaxStoredJsonCharacters })
            {
                cutShort++;
            }

            if (Holds(call.ResultJson, value, comparison))
            {
                matches.Add(new NhAssistantToolResultMatch(call.Id, call.ToolId, Holds(call.ArgumentsJson, value, comparison)));
            }
        }

        return new NhAssistantToolResultSearch(matches, searched, cutShort);
    }

    /// <summary>
    /// Whether what a tool returned can back a value: not the assistant's own functions, whose results
    /// repeat what a model wrote, and not a script, whose program a model wrote. The steps of a script
    /// are calls of their own and do count.
    /// </summary>
    internal static bool IsSource(string toolId, IReadOnlySet<string> scriptTools)
    {
        return !toolId.StartsWith(AssistantFunctionPrefix, StringComparison.Ordinal) && !scriptTools.Contains(toolId);
    }

    /// <summary>
    /// Whether stored JSON holds the value in a string, a number or a property name. Text that is no
    /// JSON, such as a result cut short at the storage bound, is searched as it is stored.
    /// </summary>
    internal static bool Holds(string? json, string value, StringComparison comparison)
    {
        if (string.IsNullOrEmpty(json))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            return ElementHolds(document.RootElement, value, comparison);
        }
        catch (JsonException)
        {
            // Stored JSON escapes a quote as " or as \", depending on who serialized it.
            return json.Contains(value, comparison)
                || json.Contains(JsonEncodedText.Encode(value).ToString(), comparison)
                || json.Contains(JsonEncodedText.Encode(value, JavaScriptEncoder.UnsafeRelaxedJsonEscaping).ToString(), comparison);
        }
    }

    private static bool ElementHolds(JsonElement element, string value, StringComparison comparison)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                return element.GetString()!.Contains(value, comparison);
            case JsonValueKind.Number:
                return element.GetRawText().Contains(value, comparison);
            case JsonValueKind.Array:
                return element.EnumerateArray().Any(item => ElementHolds(item, value, comparison));
            case JsonValueKind.Object:
                return element.EnumerateObject().Any(property =>
                    property.Name.Contains(value, comparison) || ElementHolds(property.Value, value, comparison));
            default:
                return false;
        }
    }

    private HashSet<string> ScriptToolIds()
    {
        return catalogs
            .Select(NhAiToolCatalogViews.Of)
            .SelectMany(catalog => catalog.Descriptors)
            .Where(descriptor => descriptor.CompositionFeature == NhAiCompositionFeature.Scripts)
            .Select(descriptor => descriptor.Id)
            .ToHashSet(StringComparer.Ordinal);
    }
}
