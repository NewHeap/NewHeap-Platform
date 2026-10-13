using System.Globalization;
using System.Text;
using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.AI.Chat.Runtime;

namespace NewHeap.Platform.AI.Chat.Runs;

/// <summary>
/// Writes the bounded summary every agent run ends with: when it ran, why, what the agent
/// answered and which proposals it left. The next runs of the thread start with the latest
/// summaries instead of the full history.
/// </summary>
internal sealed class NhAssistantRunSummaries(INhAssistantStore store, NhAssistantRunStore runs)
{
    internal const int MaxAnswerCharacters = 600;

    public async Task WriteAsync(AssistantRun run, string status, Guid? turnId, CancellationToken cancellationToken)
    {
        var summary = new StringBuilder()
            .Append(run.CreatedAt.UtcDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture))
            .Append(" UTC, trigger ")
            .Append(run.TriggerId)
            .Append(", ")
            .Append(status)
            .Append('.');

        var proposals = await runs.GetRunApprovalsAsync(run.Id, cancellationToken);
        if (proposals.Count > 0)
        {
            summary.Append(" Proposals: ")
                .Append(string.Join(", ", proposals.Select(proposal => proposal.ToolId + " (" + proposal.Status + ")")))
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

        await runs.SetRunSummaryAsync(run.Id, summary.ToString(), cancellationToken);
    }
}
