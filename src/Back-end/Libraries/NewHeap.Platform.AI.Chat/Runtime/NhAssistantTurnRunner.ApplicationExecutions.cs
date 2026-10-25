using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Chat.Runs;

namespace NewHeap.Platform.AI.Chat.Runtime;

/// <summary>
/// A person's inline approval of a tool the application executes with
/// <see cref="NhAssistantApplicationExecutionBuilder.AlsoInline"/> (LS17): the turn hands the proposal to
/// the application instead of running the tool's function, and goes on with what the hand-off says.
/// </summary>
internal sealed partial class NhAssistantTurnRunner
{
    /// <summary>
    /// The code of the tool result the model reads when the application took the approved proposal.
    /// </summary>
    internal const string HandedToApplicationCode = "assistant-proposal-handed-to-application";

    /// <summary>
    /// The application's execution of <paramref name="approval"/>'s tool when a person's inline approval is
    /// handed to it too; <see langword="null"/> when the tool's function runs (AE3).
    /// </summary>
    private NhAssistantApplicationExecution? InlineHandOffOf(AssistantApproval approval)
    {
        return registration.TryGetApplicationExecution(approval.ToolId, out var execution) && execution.AlsoInline
            ? execution
            : null;
    }

    /// <summary>
    /// Hands the approved proposal to the application and returns the tool result the model reads: the
    /// application has it and reports later in this conversation, it refused it, or whether it took it is
    /// unknown. The tool call keeps waiting as a deferred call does, until the report ends it.
    /// </summary>
    private async Task<Dictionary<string, object?>> HandOffInlineAsync(
        NhAssistantTurnState state,
        AssistantApproval approval,
        NhAssistantApplicationExecution execution,
        CancellationToken cancellationToken)
    {
        var handOffs = services.GetService<INhAssistantInlineHandOffs>()
            ?? throw new InvalidOperationException(
                $"Tool '{execution.ToolId}' hands inline approvals to the application, which needs the assistant's ASP.NET registration.");
        var outcome = await handOffs.HandOffInlineAsync(approval, state.Conversation, execution, cancellationToken);
        await live.PublishChangedAsync(state.Conversation.Id, CancellationToken.None);
        if (outcome is null)
        {
            state.ToolsDisabled = true;
            return new Dictionary<string, object?>
            {
                ["success"] = false,
                ["code"] = NhAssistantErrorCodes.ApprovalNotPending,
                ["message"] = "The approved action no longer waited to be carried out. Do not retry it; explain the outcome briefly."
            };
        }

        switch (outcome.Status)
        {
            case NhAssistantApprovalStatuses.AwaitingApplication:
                return new Dictionary<string, object?>
                {
                    ["success"] = false,
                    ["code"] = HandedToApplicationCode,
                    ["proposalId"] = approval.ProposalId,
                    ["reportDueAt"] = outcome.ReportDueAt,
                    ["message"] = "The person approved the action and it was handed to the application, which carries it out and reports how it went in this conversation. It has not completed yet. Do not retry it; tell the person that the application has it and that its report follows here."
                };
            case NhAssistantApprovalStatuses.Executed:
                return new Dictionary<string, object?>
                {
                    ["success"] = true,
                    ["code"] = outcome.Code,
                    ["message"] = "The application carried out the approved action at once; its report is in this conversation. Do not retry it; explain the outcome briefly."
                };
            case NhAssistantApprovalStatuses.ExecutionFailed:
                state.ToolsDisabled = true;
                return new Dictionary<string, object?>
                {
                    ["success"] = false,
                    ["code"] = outcome.Code,
                    ["message"] = "The application did not carry out the approved action. Do not retry it; explain the outcome briefly."
                };
            default:
                state.ToolsDisabled = true;
                return new Dictionary<string, object?>
                {
                    ["success"] = false,
                    ["code"] = outcome.Code,
                    ["message"] = "It is unknown whether the application carried out the approved action. Do not retry it; tell the person that it has to be checked before anyone asks again."
                };
        }
    }
}
