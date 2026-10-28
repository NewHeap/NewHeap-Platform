using Microsoft.EntityFrameworkCore;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.AI.Chat.Runtime;

namespace NewHeap.Platform.AI.Chat;

/// <summary>
/// One step of a composition or script call as the agent's activity shows it: content-free.
/// </summary>
internal sealed record NhAssistantChainStepActivity(int Index, string ToolId, string Status, string? ResultCode);

/// <summary>
/// One composition or script call of an agent (TC1): the tool calls it ran, in order, the model rounds
/// that saved and the size of the result returned to the model, in characters of JSON.
/// </summary>
internal sealed record NhAssistantChainCallActivity(
    Guid InvocationId,
    Guid ConversationId,
    string ToolId,
    NhAiCompositionFeature Feature,
    string Status,
    string? ResultCode,
    DateTimeOffset StartedAt,
    IReadOnlyList<NhAssistantChainStepActivity> Steps,
    int RoundsSaved,
    int ResultCharacters);

/// <summary>
/// The use of one composition or the script tool by an agent in a period, over the calls the activity
/// read.
/// </summary>
internal sealed record NhAssistantChainToolActivity(
    string ToolId,
    NhAiCompositionFeature Feature,
    int Calls,
    int ToolCalls,
    int RoundsSaved,
    int AverageResultCharacters,
    int MaxResultCharacters);

/// <summary>
/// An agent's composition and script calls in a period: the newest calls with their steps, the use per
/// tool over those calls, and how many calls the period holds in all.
/// </summary>
internal sealed record NhAssistantChainActivity(
    int Total,
    IReadOnlyList<NhAssistantChainToolActivity> Tools,
    IReadOnlyList<NhAssistantChainCallActivity> Calls);

/// <summary>
/// Reads what an agent's compositions and scripts ran from the tool invocations the turns stored: a
/// composition or script call is the invocation of a tool marked with
/// <see cref="NhAiToolDescriptor.CompositionFeature"/>, and its steps are the invocations of the same turn
/// whose call id names it (<see cref="NhAssistantToolStepCallIds"/>). No record of its own is added.
/// Calls in a consultation leave no invocation, as every call there does.
/// </summary>
internal sealed class NhAssistantCompositionActivity(
    NhAssistantDbContextFactory contextFactory,
    IEnumerable<INhAiToolCatalog> catalogs)
{
    /// <summary>The most calls one read returns; the totals per tool cover these.</summary>
    public const int MaxCalls = 500;

    public async Task<NhAssistantChainActivity> GetAsync(
        string agentId,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        var features = catalogs
            .Select(NhAiToolCatalogViews.Of)
            .SelectMany(catalog => catalog.Descriptors)
            .Where(descriptor => descriptor.CompositionFeature != NhAiCompositionFeature.None)
            .GroupBy(descriptor => descriptor.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().CompositionFeature, StringComparer.Ordinal);
        if (features.Count == 0)
        {
            return new NhAssistantChainActivity(0, [], []);
        }

        var toolIds = features.Keys.ToArray();
        await using var context = contextFactory.CreateDbContext();
        var calls = context.ToolInvocations
            .Where(invocation => toolIds.Contains(invocation.ToolId)
                && invocation.StartedAt >= from
                && invocation.StartedAt < to)
            .Join(
                context.Conversations.Where(conversation => conversation.AgentId == agentId),
                invocation => invocation.ConversationId,
                conversation => conversation.Id,
                (invocation, _) => invocation);
        var total = await calls.CountAsync(cancellationToken);
        var page = await calls
            .OrderByDescending(invocation => invocation.StartedAt)
            .ThenByDescending(invocation => invocation.Id)
            .Take(MaxCalls)
            .Select(invocation => new
            {
                invocation.Id,
                invocation.ConversationId,
                invocation.TurnId,
                invocation.ToolId,
                invocation.Status,
                invocation.ResultCode,
                invocation.StartedAt,
                ResultCharacters = invocation.ResultJson == null ? 0 : invocation.ResultJson.Length
            })
            .ToListAsync(cancellationToken);
        if (page.Count == 0)
        {
            return new NhAssistantChainActivity(total, [], []);
        }

        var conversationIds = page.Select(call => call.ConversationId).Distinct().ToArray();
        var turnIds = page.Select(call => call.TurnId).Distinct().ToArray();
        var stepRows = await context.ToolInvocations
            .Where(invocation => conversationIds.Contains(invocation.ConversationId)
                && turnIds.Contains(invocation.TurnId)
                && (invocation.CallId.StartsWith(NhAssistantToolStepCallIds.CompositionPrefix)
                    || invocation.CallId.StartsWith(NhAssistantToolStepCallIds.ScriptPrefix)))
            .Select(invocation => new { invocation.CallId, invocation.ToolId, invocation.Status, invocation.ResultCode })
            .ToListAsync(cancellationToken);
        var stepsByParent = new Dictionary<Guid, List<NhAssistantChainStepActivity>>();
        foreach (var row in stepRows)
        {
            if (!NhAssistantToolStepCallIds.TryParse(row.CallId, out _, out var parentId, out var index))
            {
                continue;
            }

            if (!stepsByParent.TryGetValue(parentId, out var steps))
            {
                steps = [];
                stepsByParent.Add(parentId, steps);
            }

            steps.Add(new NhAssistantChainStepActivity(index, row.ToolId, row.Status, row.ResultCode));
        }

        var activities = page
            .Select(call =>
            {
                var steps = stepsByParent.TryGetValue(call.Id, out var found)
                    ? found.OrderBy(step => step.Index).ToArray()
                    : [];
                return new NhAssistantChainCallActivity(
                    call.Id,
                    call.ConversationId,
                    call.ToolId,
                    features[call.ToolId],
                    call.Status,
                    call.ResultCode,
                    call.StartedAt,
                    steps,
                    Math.Max(0, steps.Length - 1),
                    call.ResultCharacters);
            })
            .ToArray();
        var tools = activities
            .GroupBy(call => call.ToolId, StringComparer.Ordinal)
            .Select(group => new NhAssistantChainToolActivity(
                group.Key,
                group.First().Feature,
                group.Count(),
                group.Sum(call => call.Steps.Count),
                group.Sum(call => call.RoundsSaved),
                (int)Math.Round(group.Average(call => call.ResultCharacters)),
                group.Max(call => call.ResultCharacters)))
            .OrderBy(tool => tool.ToolId, StringComparer.Ordinal)
            .ToArray();
        return new NhAssistantChainActivity(total, tools, activities);
    }
}
