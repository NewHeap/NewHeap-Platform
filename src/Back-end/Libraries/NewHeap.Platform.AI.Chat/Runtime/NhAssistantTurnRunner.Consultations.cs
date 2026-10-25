using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.AI.Chat.Runs;
using NewHeap.Platform.Common.Models;

namespace NewHeap.Platform.AI.Chat.Runtime;

/// <summary>
/// Option A of the consultation proposals: the person who asked decides a consulted agent's proposal
/// in their own conversation. The decision executes the proposal in the asker's own request, in the
/// consulted agent's scope with the asker's live rights, and the asking turn continues with the
/// consultation's answer and the outcome. The asker may also leave the proposal for the subject's
/// approvers instead (CP20).
/// </summary>
internal sealed partial class NhAssistantTurnRunner
{
    /// <summary>
    /// The proposal of a consultation that the conversation's person decides here, when
    /// <paramref name="approval"/> is one; <see langword="null"/> for the conversation's own proposals.
    /// </summary>
    private async Task<NhAssistantAskerDecisionRow?> FindAskerDecisionAsync(
        AssistantConversation conversation,
        AssistantApproval approval,
        CancellationToken cancellationToken)
    {
        if (approval.ConversationId == conversation.Id
            || approval.ConsultationId is null
            || approval.Mode != NhAssistantApprovalModes.Inline)
        {
            return null;
        }

        return (await runs.GetAskerDecisionsAsync(conversation.Id, [approval.Id], cancellationToken)).FirstOrDefault();
    }

    /// <summary>
    /// Leaves the proposal for the subject's approvers before the asking turn continues; nothing
    /// changes when no route at the subject applies.
    /// </summary>
    private async Task<TaskResult<AssistantApproval>> LeaveForApproversAsync(
        AssistantApproval approval,
        string askerActorId,
        CancellationToken cancellationToken)
    {
        var proposals = services.GetService<NhAssistantConsultationProposals>();
        if (proposals is null)
        {
            return TaskResult<AssistantApproval>.Failed(
                NhAssistantErrorCodes.ConsultProposalRefused,
                "No route at the subject applies.");
        }

        return await proposals.MoveToSubjectAsync(approval, askerActorId, cancellationToken);
    }

    /// <summary>
    /// Continues the asking turn after its person decided, or left, the proposal a consultation left
    /// for them.
    /// </summary>
    private async Task RunAskerDecisionTurnAsync(
        NhAssistantConversationAccess access,
        NhAssistantAgent effectiveAgent,
        NhAssistantAskerDecisionRow decision,
        Guid turnId,
        NhAssistantDecisionTurnRequest request,
        AssistantApproval? movedTo,
        ChannelWriter<NhAssistantTurnEvent> events,
        CancellationToken cancellationToken)
    {
        var conversation = access.Conversation;
        var agent = effectiveAgent.Definition;
        var approval = decision.Approval;
        await live.PublishChangedAsync(conversation.Id, CancellationToken.None);
        var history = await store.GetMessagesAsync(
            conversation.Id,
            registration.Limits.MaxHistoryMessages,
            CancellationToken.None);
        var userMessage = history
            .LastOrDefault(message => message.TurnId == turnId && message.Role == NhAssistantMessageRoles.User);
        var clientContext = NhAssistantClientContext.FromStorage(userMessage?.ClientContextJson);
        var speakers = await LoadSpeakersAsync(conversation, access.ActorId, request.Language);
        var state = await CreateStateAsync(conversation, effectiveAgent, turnId, userMessage?.Id ?? Guid.Empty, request.CallerContext, request.Language, clientContext, speakers, events, request.AgentTurn);
        state.Scope.Channel = request.Channel;
        var interceptor = CreateInterceptor(state);
        await state.EmitAsync(new NhAssistantTurnStartedEvent(turnId, state.UserMessageId, state.AssistantMessageId));

        var askingCall = await runs.FindAskingCallAsync(conversation.Id, approval.Id, CancellationToken.None)
            ?? throw new InvalidOperationException("The consult call that waits for the asker's decision is missing.");
        var asker = request.CallerContext.ActorId;
        var now = DateTimeOffset.UtcNow;
        var decidedAt = _applicationClock.GetUtcNow();
        NhAssistantAskerOutcome outcome;
        if (movedTo is not null)
        {
            outcome = new NhAssistantAskerOutcome(NhAssistantConsultationOutcomes.Moved, NhAssistantErrorCodes.ConsultProposalMoved)
            {
                MovedToApprovalId = movedTo.Id
            };
        }
        else if (decidedAt >= approval.ExpiresAt)
        {
            await runs.TryTransitionApprovalAsync(
                approval.Id,
                [NhAssistantApprovalStatuses.Pending],
                NhAssistantApprovalStatuses.Expired,
                NhAssistantApprovalTransition.None,
                CancellationToken.None);
            await runs.EndAskingCallAsync(conversation.Id, approval.Id, NhAssistantToolCallStatuses.Failed, NhAssistantErrorCodes.ApprovalExpired, CancellationToken.None);
            await interceptor.RecordBusinessEventAsync(
                AskerDecisionEvent(state, approval, askingCall, NhAssistantAuditEventKind.ApprovalExpired, NhAssistantErrorCodes.ApprovalExpired, now),
                CancellationToken.None);
            await FinishWithErrorAsync(state, NhAssistantErrorCodes.ApprovalExpired, NhAssistantConversationStatuses.Idle);
            return;
        }
        else if (request.Approve)
        {
            outcome = await ApproveAndExecuteAsync(state, interceptor, decision, askingCall, asker, decidedAt, now, cancellationToken);
        }
        else
        {
            var rejected = await runs.TryTransitionApprovalAsync(
                approval.Id,
                [NhAssistantApprovalStatuses.Pending],
                NhAssistantApprovalStatuses.Rejected,
                new NhAssistantApprovalTransition
                {
                    DecidedByActorId = asker,
                    DecidedAt = decidedAt,
                    Reason = request.Reason
                },
                CancellationToken.None);
            if (!rejected)
            {
                await FinishWithErrorAsync(state, NhAssistantErrorCodes.ApprovalNotPending, NhAssistantConversationStatuses.Idle);
                return;
            }

            await interceptor.RecordBusinessEventAsync(
                AskerDecisionEvent(state, approval, askingCall, NhAssistantAuditEventKind.ApprovalRejected, NhAssistantApprovalStatuses.Rejected, now),
                CancellationToken.None);
            // The asker declined their own request; the model gives a closing answer without tools.
            state.ToolsDisabled = true;
            outcome = new NhAssistantAskerOutcome(NhAssistantConsultationOutcomes.Rejected, NhAssistantApprovalStatuses.Rejected)
            {
                Reason = request.Reason
            };
        }

        // The consult call ends with the consultation's answer and what became of its proposal.
        var functionResult = AskerDecisionResult(askingCall.ResultJson, approval.Id, outcome);
        askingCall.Status = NhAssistantToolCallStatuses.Succeeded;
        askingCall.ResultCode = null;
        askingCall.ResultJson = NhAssistantContent.SerializeResult(functionResult);
        askingCall.CompletedAt = DateTimeOffset.UtcNow;
        await store.UpdateToolInvocationAsync(askingCall, CancellationToken.None);
        await state.EmitAsync(new NhAssistantToolCompletedEvent(
            askingCall.Id,
            askingCall.Status,
            null,
            NhAssistantContent.ResultPreview(askingCall.ResultJson, askingCall.DataClassification)));

        var messages = await BuildHistoryAsync(conversation.Id, history, agent, null, askingCall.Id, speakers, state: state);
        messages.Add(new ChatMessage(
            ChatRole.Assistant,
            [new FunctionCallContent(askingCall.CallId, askingCall.FunctionName, NhAssistantContent.DeserializeArguments(askingCall.ArgumentsJson))]));
        messages.Add(new ChatMessage(
            ChatRole.Tool,
            [new FunctionResultContent(askingCall.CallId, functionResult)]));
        await RunModelAsync(state, request.CallerContext, messages, cancellationToken);
    }

    /// <summary>
    /// Records the asker's approval and executes the stored call in the consulted agent's scope, in the
    /// asker's own request: the subject's authorization resource, no grants, the asker as initiator in
    /// the request, so the invocation gate uses the request's own principal and none is rebuilt.
    /// </summary>
    private async Task<NhAssistantAskerOutcome> ApproveAndExecuteAsync(
        NhAssistantTurnState state,
        NhAssistantToolCallInterceptor interceptor,
        NhAssistantAskerDecisionRow decision,
        AssistantToolInvocation askingCall,
        string asker,
        DateTimeOffset decidedAt,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var approval = decision.Approval;
        var thread = decision.Thread;
        var approved = await runs.TryTransitionApprovalAsync(
            approval.Id,
            [NhAssistantApprovalStatuses.Pending],
            NhAssistantApprovalStatuses.Approved,
            new NhAssistantApprovalTransition
            {
                DecidedByActorId = asker,
                DecidedAt = decidedAt,
                ApprovalExpiresAt = approval.ExpiresAt
            },
            CancellationToken.None);
        if (!approved)
        {
            return new NhAssistantAskerOutcome(NhAssistantConsultationOutcomes.Failed, NhAssistantErrorCodes.ApprovalNotPending);
        }

        await interceptor.RecordBusinessEventAsync(
            AskerDecisionEvent(state, approval, askingCall, NhAssistantAuditEventKind.ApprovalApproved, NhAssistantApprovalStatuses.Approved, now),
            CancellationToken.None);

        var subject = await services.GetRequiredService<NhAssistantSubjectAccessService>()
            .DescribeAsync(new NhAiSubjectRef(thread.SubjectType, thread.SubjectId), cancellationToken);
        NhAssistantProposalExecutionResult execution;
        if (subject is null || !registration.AgentRuns.TryGetValue(thread.AgentId, out var options))
        {
            execution = new NhAssistantProposalExecutionResult(false, NhAssistantErrorCodes.SubjectNotFound);
        }
        else
        {
            var proposal = Governance.NhAssistantProposalSerializer.Deserialize(approval.ProposalJson);
            var consulted = new NhAssistantAgentTurnContext(
                thread,
                subject,
                options,
                null,
                NhAssistantTriggerIds.ProposalDecided,
                asker,
                proposal.AccountableOwnerId,
                IsRequestTurn: true)
            {
                DelegatedOnly = true
            };
            execution = await ExecuteProposalAsync(new NhAssistantProposalExecutionRequest(consulted, approval.Id), cancellationToken);
        }

        var status = execution.Succeeded ? NhAssistantApprovalStatuses.Executed : NhAssistantApprovalStatuses.ExecutionFailed;
        await runs.TryTransitionApprovalAsync(
            approval.Id,
            [NhAssistantApprovalStatuses.Approved],
            status,
            new NhAssistantApprovalTransition
            {
                ExecutedAt = DateTimeOffset.UtcNow,
                ExecutionResultCode = execution.ResultCode
            },
            CancellationToken.None);
        var closed = await store.FindApprovalAsync(approval.Id, CancellationToken.None) ?? approval;
        await services.GetRequiredService<NhAssistantAgentEvents>().ProposalAsync(
            NhAssistantAuditEventKind.AgentProposalExecuted,
            thread,
            closed,
            asker,
            status,
            CancellationToken.None);
        await live.PublishChangedAsync(thread.ConversationId, CancellationToken.None);

        var executed = await store.FindToolInvocationAsync(approval.ToolInvocationId, CancellationToken.None);
        return new NhAssistantAskerOutcome(
            execution.Succeeded ? NhAssistantConsultationOutcomes.Executed : NhAssistantConsultationOutcomes.Failed,
            execution.Succeeded ? NhAssistantApprovalStatuses.Executed : execution.ResultCode ?? NhAiToolFailureCodes.Failed)
        {
            ResultJson = executed?.ResultJson
        };
    }

    /// <summary>
    /// Rejects the proposal of a consultation the conversation waits for, when its person cancels the
    /// turn. Returns whether one waited.
    /// </summary>
    private async Task<bool> RejectAskerDecisionAsync(
        AssistantConversation conversation,
        string actorId,
        CancellationToken cancellationToken)
    {
        if (conversation.ActiveTurnId is not { } turnId
            || await runs.FindPendingAskerDecisionAsync(conversation.Id, turnId, cancellationToken) is not { } pending)
        {
            return false;
        }

        if (await runs.TryTransitionApprovalAsync(
                pending.Id,
                [NhAssistantApprovalStatuses.Pending],
                NhAssistantApprovalStatuses.Rejected,
                new NhAssistantApprovalTransition
                {
                    DecidedByActorId = actorId,
                    DecidedAt = _applicationClock.GetUtcNow()
                },
                cancellationToken))
        {
            await runs.EndAskingCallAsync(
                conversation.Id,
                pending.Id,
                NhAssistantToolCallStatuses.Rejected,
                NhAssistantErrorCodes.ApprovalNotPending,
                cancellationToken);
        }

        return true;
    }

    /// <summary>
    /// The consult call's result after the asker's decision: the consultation's answer as the call
    /// returned it, and what became of its proposal, which the asking model continues with.
    /// </summary>
    private static JsonElement AskerDecisionResult(string? consultResultJson, Guid approvalId, NhAssistantAskerOutcome outcome)
    {
        JsonObject result;
        try
        {
            result = consultResultJson is not null && JsonNode.Parse(consultResultJson) is JsonObject parsed
                ? parsed
                : new JsonObject { ["success"] = true };
        }
        catch (JsonException)
        {
            result = new JsonObject { ["success"] = true };
        }

        var decision = new JsonObject
        {
            ["approvalId"] = approvalId,
            ["outcome"] = outcome.Outcome,
            ["code"] = outcome.Code,
            ["message"] = outcome.Outcome switch
            {
                NhAssistantConsultationOutcomes.Executed =>
                    "The person who asked approved the proposal and it was executed in their own request. Tell them the outcome briefly.",
                NhAssistantConsultationOutcomes.Rejected =>
                    "The person who asked rejected the proposal; nothing was executed. Do not retry it; explain the outcome briefly.",
                NhAssistantConsultationOutcomes.Moved =>
                    "The person who asked left the proposal for the subject's approvers; it now waits for them and nothing was executed. Do not retry it; tell the person who decides it.",
                _ => "The proposal was not executed. Do not retry it; explain the outcome briefly."
            }
        };
        if (outcome.Reason is { Length: > 0 } reason)
        {
            decision["reason"] = reason;
        }
        if (outcome.MovedToApprovalId is { } movedTo)
        {
            decision["movedToApprovalId"] = movedTo;
        }
        if (outcome.ResultJson is { Length: > 0 and <= 8_000 } executed)
        {
            try
            {
                decision["result"] = JsonNode.Parse(executed);
            }
            catch (JsonException)
            {
                // A bounded result that no longer parses stays out; the code tells the outcome.
            }
        }

        result["decision"] = decision;
        return JsonSerializer.SerializeToElement(result, NhAssistantContent.JsonOptions);
    }

    private static NhAssistantAuditEvent AskerDecisionEvent(
        NhAssistantTurnState state,
        AssistantApproval approval,
        AssistantToolInvocation askingCall,
        NhAssistantAuditEventKind kind,
        string code,
        DateTimeOffset occurredAt)
    {
        return DecisionEvent(state, approval, askingCall, kind, code, occurredAt) with
        {
            ToolId = approval.ToolId,
            InvocationId = approval.ToolInvocationId,
            ConsultationId = approval.ConsultationId,
            ConsultationRoute = NhAssistantConsultationRoutes.AskerDecides,
            GrantSource = approval.GrantSource,
            ApprovalPolicyId = approval.PolicyId
        };
    }
}

/// <summary>
/// What became of a proposal the asker decided in their own conversation.
/// </summary>
/// <param name="Outcome">One of <see cref="NhAssistantConsultationOutcomes"/>.</param>
/// <param name="Code">The approval status or failure code.</param>
internal sealed record NhAssistantAskerOutcome(string Outcome, string Code)
{
    public string? Reason { get; init; }

    public Guid? MovedToApprovalId { get; init; }

    /// <summary>
    /// The stored result of the executed call.
    /// </summary>
    public string? ResultJson { get; init; }
}

/// <summary>
/// The outcomes of a proposal the asker decided, as the asking model reads them.
/// </summary>
internal static class NhAssistantConsultationOutcomes
{
    public const string Executed = "executed";
    public const string Failed = "execution-failed";
    public const string Rejected = "rejected";
    public const string Moved = "moved";
}
