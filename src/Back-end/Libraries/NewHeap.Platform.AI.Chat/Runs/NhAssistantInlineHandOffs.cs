using NewHeap.Platform.AI.Chat.Entities;

namespace NewHeap.Platform.AI.Chat.Runs;

/// <summary>
/// Hands a person's inline approval of a tool the application executes with
/// <see cref="NhAssistantApplicationExecutionBuilder.AlsoInline"/> to the application's
/// <see cref="INhAssistantProposalExecutor"/>, as a deferred approval is handed (LS17). The ASP.NET
/// package implements it with the same hand-off, report and deadline.
/// </summary>
internal interface INhAssistantInlineHandOffs
{
    /// <summary>
    /// Hands the approved proposal of <paramref name="conversation"/> to the application: a chat of the
    /// person, or the agent thread they took a turn in. Returns how the hand-off ended, or
    /// <see langword="null"/> when the proposal was no longer approved.
    /// </summary>
    Task<NhAssistantHandOffOutcome?> HandOffInlineAsync(
        AssistantApproval approval,
        AssistantConversation conversation,
        NhAssistantApplicationExecution execution,
        CancellationToken cancellationToken);
}

/// <summary>
/// How a hand-off to the application ended.
/// </summary>
/// <param name="Status">The proposal's status afterwards: <see cref="NhAssistantApprovalStatuses.AwaitingApplication"/>
/// while it waits for the report, <see cref="NhAssistantApprovalStatuses.ExecutionFailed"/> when the executor
/// refused it, <see cref="NhAssistantApprovalStatuses.ExecutionUnknown"/> when it threw, or the status of a
/// report the executor made at once.</param>
/// <param name="Code">The proposal's execution result, such as the refusal's code; <see langword="null"/>
/// while it waits.</param>
/// <param name="ReportDueAt">Until when the assistant waits for the report, on the application's clock.</param>
internal sealed record NhAssistantHandOffOutcome(string Status, string? Code, DateTimeOffset ReportDueAt);
