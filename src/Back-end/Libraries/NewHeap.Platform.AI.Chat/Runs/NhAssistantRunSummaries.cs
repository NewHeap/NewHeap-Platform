using System.Globalization;
using System.Text;
using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.AI.Chat.Runtime;

namespace NewHeap.Platform.AI.Chat.Runs;

/// <summary>
/// Composes the bounded summary every agent run ends with: when it ran, why, what the agent
/// answered and which proposals it left. The next runs of the thread start with the latest
/// summaries instead of the full history. The run's end stores the summary in the same write that
/// ends the run (<see cref="NhAssistantRunStore.TrySetRunStatusAsync"/>), so a reader never sees an
/// ended run without it.
/// </summary>
internal sealed class NhAssistantRunSummaries(INhAssistantStore store, NhAssistantRunStore runs)
{
    internal const int MaxAnswerCharacters = 600;

    /// <summary>
    /// The summary of <paramref name="run"/> as it ends with <paramref name="status"/>, with the answer
    /// of <paramref name="turnId"/> when the run answered.
    /// </summary>
    public async Task<string> ComposeAsync(AssistantRun run, string status, Guid? turnId, CancellationToken cancellationToken)
    {
        var summary = new StringBuilder()
            .Append(run.CreatedAt.UtcDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture))
            .Append(" UTC, trigger ")
            .Append(run.TriggerId)
            .Append(", ")
            .Append(status)
            .Append('.');

        var proposals = await runs.GetRunApprovalsAsync(run.Id, cancellationToken);
        var own = proposals.Where(proposal => proposal.PlacedFromConversationId is null).ToArray();
        if (own.Length > 0)
        {
            summary.Append(" Proposals: ")
                .Append(string.Join(", ", own.Select(proposal => proposal.ToolId + " (" + proposal.Status + ")")))
                .Append('.');
        }

        // Proposals placed at other objects wait there; the summary counts them per tool and status.
        var placed = proposals
            .Where(proposal => proposal.PlacedFromConversationId is not null)
            .GroupBy(proposal => (proposal.ToolId, proposal.Status))
            .OrderBy(group => group.Key.ToolId, StringComparer.Ordinal)
            .ThenBy(group => group.Key.Status, StringComparer.Ordinal)
            .ToArray();
        if (placed.Length > 0)
        {
            summary.Append(" Placed at other objects: ")
                .Append(string.Join(", ", placed.Select(group => group.Count().ToString(CultureInfo.InvariantCulture) + " x " + group.Key.ToolId + " (" + group.Key.Status + ")")))
                .Append('.');
        }

        // The answer is free text and comes last, so its own punctuation ends the summary.
        if (turnId is { } turn)
        {
            var answer = (await store.GetMessagesAsync(run.ConversationId, 20, cancellationToken))
                .Where(message => message.TurnId == turn && message.Role == NhAssistantMessageRoles.Assistant)
                .Select(message => NhAssistantContent.TextOf(NhAssistantContent.DeserializeParts(message.PartsJson)))
                .LastOrDefault(text => !string.IsNullOrWhiteSpace(text));
            if (answer is not null)
            {
                var flat = answer.ReplaceLineEndings(" ").Trim();
                summary.Append(" Answer: ")
                    .Append(flat.Length > MaxAnswerCharacters ? flat[..MaxAnswerCharacters] + "…" : flat);
            }
        }

        return summary.ToString();
    }
}
