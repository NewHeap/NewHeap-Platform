using System.Globalization;
using NewHeap.Platform.AI.Chat.Runtime;
using NewHeap.Platform.Common.Models;

namespace NewHeap.Platform.AI.Chat.Governance;

/// <summary>
/// Decorates the application's invocation gate. The inner gate still authenticates the caller and
/// authorizes every tool policy as the signed-in user. Inside an assistant tool call the authorized
/// context is then bound to the turn: the agent acts as a non-human actor on behalf of the
/// accountable user, with the turn id as run id, the agent prompt identity, the turn deadline,
/// the call's idempotency key and, for an approved retry, the proposal and approval ids.
/// </summary>
internal sealed class NhAssistantInvocationGate(INhAiToolInvocationGate inner) : INhAiToolInvocationGate
{
    public const string Purpose = "assistant";
    public const string ActorMismatchCode = "assistant-actor-mismatch";

    public async ValueTask<TaskResult<NhAiInvocationContext>> AuthorizeAsync(
        NhAiToolDescriptor descriptor,
        CancellationToken cancellationToken = default)
    {
        var authorized = await inner.AuthorizeAsync(descriptor, cancellationToken);
        var turn = NhAssistantExecutionScope.CurrentTurn;
        var call = NhAssistantExecutionScope.CurrentCall;
        if (!authorized.Success || turn is null || call is null)
        {
            return authorized;
        }

        var context = authorized.Data;
        if (!string.Equals(context.ActorId, turn.OwnerActorId, StringComparison.Ordinal))
        {
            // The request principal is not the owner of the running turn.
            return TaskResult<NhAiInvocationContext>.Failed(
                ActorMismatchCode,
                "The assistant tool call does not belong to the authenticated actor.");
        }

        var deadline = context.Deadline is { } existing && existing < turn.Deadline
            ? existing
            : turn.Deadline;
        var remainingCalls = Math.Max(1, turn.Limits.MaxToolCallsPerTurn - turn.ToolCalls + 1);
        return TaskResult<NhAiInvocationContext>.Succeeded(context with
        {
            InvocationId = call.InvocationId,
            ActorId = turn.Agent.ActorId,
            ActorKind = NhAiActorKind.Agent,
            AccountableOwnerId = turn.OwnerActorId,
            Purpose = Purpose,
            RunId = turn.TurnId.ToString(),
            CorrelationId = context.CorrelationId ?? turn.TurnId.ToString(),
            AgentVersion = turn.Agent.Version.ToString(CultureInfo.InvariantCulture),
            ModelProfileName = turn.ModelProfileName,
            PromptVersion = turn.PromptVersion,
            PromptHash = turn.PromptHash,
            Deadline = deadline,
            RemainingBudget = new NhAiModelBudget(int.MaxValue, int.MaxValue, remainingCalls, null),
            IdempotencyKey = call.IdempotencyKey,
            ProposalId = call.ProposalId?.ToString(),
            ApprovalId = call.ApprovalId?.ToString()
        });
    }
}
