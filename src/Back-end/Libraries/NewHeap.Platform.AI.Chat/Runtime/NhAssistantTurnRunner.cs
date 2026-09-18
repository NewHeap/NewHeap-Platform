using System.Globalization;
using System.Threading.Channels;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using NewHeap.Platform.AI.AgentFramework;
using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Chat.Governance;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.Common.Models;

namespace NewHeap.Platform.AI.Chat.Runtime;

/// <summary>
/// Orchestrates assistant turns: it claims the conversation, persists messages incrementally,
/// runs the agent through the NewHeap Agent Framework adapter, intercepts governed tool calls,
/// pauses for approval and resumes with the human decision. It logs identifiers and counts only.
/// </summary>
internal sealed class NhAssistantTurnRunner(
    IServiceProvider services,
    INhAssistantStore store,
    NhAssistantAgentRegistry agents,
    NhAssistantRegistrationState registration,
    NhAssistantTurnCancellationRegistry cancellations,
    INhAiModelProfileRegistry profiles,
    INhAiAgentFrameworkAdapter adapter,
    INhAiToolDiscoveryService discovery,
    IEnumerable<INhAiToolCatalog> catalogs,
    INhAiProposalFactory proposalFactory,
    IEnumerable<INhAssistantBusinessAuditSink> businessSinks,
    INhAssistantTitleGenerator titleGenerator,
    ILogger<NhAssistantTurnRunner> logger) : INhAssistantTurnRunner
{
    private static readonly string[] MessageStartStatuses =
    [
        NhAssistantConversationStatuses.Idle,
        NhAssistantConversationStatuses.Failed
    ];

    private static readonly string[] DecisionStartStatuses =
    [
        NhAssistantConversationStatuses.WaitingForApproval
    ];

    private readonly IReadOnlyList<INhAssistantBusinessAuditSink> _businessSinks = businessSinks.ToArray();
    private readonly IReadOnlyList<INhAiToolCatalog> _catalogs = catalogs.ToArray();

    public async Task<TaskResult<NhAssistantTurnHandle>> StartMessageTurnAsync(
        NhAssistantMessageTurnRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var limits = registration.Limits;
        var owner = request.CallerContext.ActorId;
        var text = request.Text?.Trim() ?? string.Empty;
        if (text.Length == 0 || request.ClientMessageId is { Length: > 128 })
        {
            return Failed(NhAssistantErrorCodes.MessageInvalid, "The assistant message is empty or invalid.");
        }
        if (text.Length > limits.MaxMessageChars)
        {
            return Failed(NhAssistantErrorCodes.MessageTooLong, "The assistant message is too long.");
        }

        var conversation = await store.FindConversationAsync(request.ConversationId, owner, cancellationToken);
        if (conversation is null)
        {
            return Failed(NhAssistantErrorCodes.ConversationNotFound, "The assistant conversation was not found.");
        }
        if (!agents.TryGet(conversation.AgentId, out var agent))
        {
            return Failed(NhAssistantErrorCodes.AgentNotFound, "The assistant agent is no longer available.");
        }
        if (!string.IsNullOrWhiteSpace(request.ClientMessageId)
            && await store.ClientMessageExistsAsync(conversation.Id, request.ClientMessageId, cancellationToken))
        {
            return Failed(NhAssistantErrorCodes.MessageDuplicate, "The assistant message was already received.");
        }

        var turnId = Guid.NewGuid();
        var claimed = await store.TryBeginTurnAsync(
            conversation.Id,
            owner,
            MessageStartStatuses,
            turnId,
            StaleBefore(limits),
            cancellationToken);
        if (!claimed)
        {
            return Failed(NhAssistantErrorCodes.ConversationBusy, "The assistant conversation already has an active turn.");
        }

        return Start(
            conversation,
            turnId,
            request.RequestAborted,
            (events, token) => RunMessageTurnAsync(conversation, agent, turnId, request, text, events, token));
    }

    public async Task<TaskResult<NhAssistantTurnHandle>> StartDecisionTurnAsync(
        NhAssistantDecisionTurnRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var owner = request.CallerContext.ActorId;
        if (request.Reason is { Length: > 500 } || string.IsNullOrWhiteSpace(request.ExpectedProposalHash))
        {
            return Failed(NhAssistantErrorCodes.ApprovalDecisionInvalid, "The approval decision is invalid.");
        }

        var conversation = await store.FindConversationAsync(request.ConversationId, owner, cancellationToken);
        if (conversation is null)
        {
            return Failed(NhAssistantErrorCodes.ConversationNotFound, "The assistant conversation was not found.");
        }
        var approval = await store.FindApprovalAsync(request.ApprovalId, cancellationToken);
        if (approval is null || approval.ConversationId != conversation.Id)
        {
            return Failed(NhAssistantErrorCodes.ApprovalNotFound, "The approval was not found.");
        }
        if (approval.Status != NhAssistantApprovalStatuses.Pending
            || conversation.Status != NhAssistantConversationStatuses.WaitingForApproval
            || conversation.ActiveTurnId != approval.TurnId)
        {
            return Failed(NhAssistantErrorCodes.ApprovalNotPending, "The approval is no longer pending.");
        }
        if (!string.Equals(approval.ProposalHash, request.ExpectedProposalHash, StringComparison.Ordinal))
        {
            return Failed(NhAssistantErrorCodes.ProposalHashMismatch, "The approval does not match the expected proposal.");
        }
        if (!agents.TryGet(conversation.AgentId, out var agent))
        {
            return Failed(NhAssistantErrorCodes.AgentNotFound, "The assistant agent is no longer available.");
        }
        if (string.Equals(owner, agent.ActorId, StringComparison.Ordinal))
        {
            // An agent identity can never decide about its own proposal.
            return Failed(NhAssistantErrorCodes.ApprovalDecisionInvalid, "The approval decision is invalid.");
        }

        var claimed = await store.TryBeginTurnAsync(
            conversation.Id,
            owner,
            DecisionStartStatuses,
            approval.TurnId,
            DateTimeOffset.MinValue,
            cancellationToken);
        if (!claimed)
        {
            return Failed(NhAssistantErrorCodes.ConversationBusy, "The assistant conversation already has an active turn.");
        }

        return Start(
            conversation,
            approval.TurnId,
            request.RequestAborted,
            (events, token) => RunDecisionTurnAsync(conversation, agent, approval, request, events, token));
    }

    public async Task CancelAsync(
        Guid conversationId,
        string ownerActorId,
        CancellationToken cancellationToken)
    {
        var conversation = await store.FindConversationAsync(conversationId, ownerActorId, cancellationToken);
        if (conversation is null)
        {
            return;
        }
        if (conversation.Status == NhAssistantConversationStatuses.Running)
        {
            cancellations.Cancel(conversationId);
            return;
        }
        if (conversation.Status != NhAssistantConversationStatuses.WaitingForApproval)
        {
            return;
        }

        var pending = await store.FindPendingApprovalAsync(conversationId, cancellationToken);
        if (pending is not null
            && await store.TryDecideApprovalAsync(
                pending.Id,
                NhAssistantApprovalStatuses.Rejected,
                ownerActorId,
                DateTimeOffset.UtcNow,
                null,
                null,
                cancellationToken))
        {
            var invocation = await store.FindToolInvocationAsync(pending.ToolInvocationId, cancellationToken);
            if (invocation is not null)
            {
                invocation.Status = NhAssistantToolCallStatuses.Rejected;
                invocation.ResultCode = NhAssistantErrorCodes.ApprovalNotPending;
                invocation.CompletedAt = DateTimeOffset.UtcNow;
                await store.UpdateToolInvocationAsync(invocation, cancellationToken);
            }
        }
        await store.TryReleaseWaitingConversationAsync(conversationId, ownerActorId, cancellationToken);
    }

    private TaskResult<NhAssistantTurnHandle> Start(
        AssistantConversation conversation,
        Guid turnId,
        CancellationToken requestAborted,
        Func<ChannelWriter<NhAssistantTurnEvent>, CancellationToken, Task> run)
    {
        var channel = Channel.CreateUnbounded<NhAssistantTurnEvent>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });
        var cancellation = cancellations.Register(conversation.Id, turnId, requestAborted);
        var completion = Task.Run(async () =>
        {
            try
            {
                await run(channel.Writer, cancellation.Token);
            }
            finally
            {
                cancellations.Unregister(conversation.Id, turnId);
                cancellation.Dispose();
                channel.Writer.TryComplete();
            }
        });
        return TaskResult<NhAssistantTurnHandle>.Succeeded(new NhAssistantTurnHandle(channel.Reader, completion));
    }

    private async Task RunMessageTurnAsync(
        AssistantConversation conversation,
        NhAssistantAgentDefinition agent,
        Guid turnId,
        NhAssistantMessageTurnRequest request,
        string text,
        ChannelWriter<NhAssistantTurnEvent> events,
        CancellationToken cancellationToken)
    {
        var history = await store.GetMessagesAsync(
            conversation.Id,
            registration.Limits.MaxHistoryMessages,
            CancellationToken.None);
        var userMessage = new AssistantMessage
        {
            Id = Guid.NewGuid(),
            ConversationId = conversation.Id,
            TurnId = turnId,
            Role = NhAssistantMessageRoles.User,
            PartsJson = NhAssistantContent.SerializeParts([new NhAssistantStoredPart(NhAssistantStoredPart.TextType, Text: text)]),
            ClientMessageId = string.IsNullOrWhiteSpace(request.ClientMessageId) ? null : request.ClientMessageId,
            CreatedAt = DateTimeOffset.UtcNow
        };
        await store.AddMessageAsync(userMessage, CancellationToken.None);
        if (history.All(message => message.Role != NhAssistantMessageRoles.User))
        {
            await GenerateTitleAsync(conversation, agent, text);
        }

        var state = await CreateStateAsync(conversation, agent, turnId, userMessage.Id, request.CallerContext, events);
        await state.EmitAsync(new NhAssistantTurnStartedEvent(turnId, userMessage.Id, state.AssistantMessageId));

        var toolCallsToday = await store.GetToolCallsAsync(
            state.Scope.OwnerActorId,
            DateOnly.FromDateTime(DateTime.UtcNow),
            CancellationToken.None);
        if (toolCallsToday >= state.Scope.Limits.DailyToolCallBudgetPerActor)
        {
            await FinishWithErrorAsync(state, NhAssistantErrorCodes.BudgetExhausted, NhAssistantConversationStatuses.Idle);
            return;
        }

        var messages = BuildHistory(history, agent, text);
        messages.Add(new ChatMessage(ChatRole.User, text));
        await RunModelAsync(state, request.CallerContext, messages, cancellationToken);
    }

    private async Task RunDecisionTurnAsync(
        AssistantConversation conversation,
        NhAssistantAgentDefinition agent,
        AssistantApproval approval,
        NhAssistantDecisionTurnRequest request,
        ChannelWriter<NhAssistantTurnEvent> events,
        CancellationToken cancellationToken)
    {
        var history = await store.GetMessagesAsync(
            conversation.Id,
            registration.Limits.MaxHistoryMessages,
            CancellationToken.None);
        var userMessageId = history
            .LastOrDefault(message => message.TurnId == approval.TurnId && message.Role == NhAssistantMessageRoles.User)
            ?.Id ?? Guid.Empty;
        var state = await CreateStateAsync(conversation, agent, approval.TurnId, userMessageId, request.CallerContext, events);
        var interceptor = CreateInterceptor(state);
        await state.EmitAsync(new NhAssistantTurnStartedEvent(approval.TurnId, userMessageId, state.AssistantMessageId));

        var invocation = await store.FindToolInvocationAsync(approval.ToolInvocationId, CancellationToken.None)
            ?? throw new InvalidOperationException("The tool invocation of a pending approval is missing.");
        var now = DateTimeOffset.UtcNow;
        var decider = request.CallerContext.ActorId;
        if (now >= approval.ExpiresAt)
        {
            await store.TryDecideApprovalAsync(
                approval.Id,
                NhAssistantApprovalStatuses.Expired,
                null,
                now,
                null,
                null,
                CancellationToken.None);
            invocation.Status = NhAssistantToolCallStatuses.Failed;
            invocation.ResultCode = NhAssistantErrorCodes.ApprovalExpired;
            invocation.CompletedAt = now;
            await store.UpdateToolInvocationAsync(invocation, CancellationToken.None);
            await interceptor.RecordBusinessEventAsync(
                DecisionEvent(state, approval, invocation, NhAssistantAuditEventKind.ApprovalExpired, NhAssistantErrorCodes.ApprovalExpired, now),
                CancellationToken.None);
            await FinishWithErrorAsync(state, NhAssistantErrorCodes.ApprovalExpired, NhAssistantConversationStatuses.Idle);
            return;
        }

        var decisionStatus = request.Approve ? NhAssistantApprovalStatuses.Approved : NhAssistantApprovalStatuses.Rejected;
        var approvalExpiresAt = request.Approve ? approval.ExpiresAt : (DateTimeOffset?)null;
        var decided = await store.TryDecideApprovalAsync(
            approval.Id,
            decisionStatus,
            decider,
            now,
            approvalExpiresAt,
            request.Reason,
            CancellationToken.None);
        if (!decided)
        {
            await FinishWithErrorAsync(state, NhAssistantErrorCodes.ApprovalNotPending, NhAssistantConversationStatuses.Idle);
            return;
        }
        await interceptor.RecordBusinessEventAsync(
            DecisionEvent(
                state,
                approval,
                invocation,
                request.Approve ? NhAssistantAuditEventKind.ApprovalApproved : NhAssistantAuditEventKind.ApprovalRejected,
                decisionStatus,
                now),
            CancellationToken.None);

        object? functionResult;
        if (request.Approve)
        {
            var execution = await ExecuteApprovedAsync(state, interceptor, approval, invocation, cancellationToken);
            if (execution is null)
            {
                return;
            }
            functionResult = execution.Result;
        }
        else
        {
            invocation.Status = NhAssistantToolCallStatuses.Rejected;
            invocation.ResultCode = "assistant-approval-rejected";
            invocation.CompletedAt = now;
            await store.UpdateToolInvocationAsync(invocation, CancellationToken.None);
            state.ToolsDisabled = true;
            functionResult = new Dictionary<string, object?>
            {
                ["success"] = false,
                ["code"] = "assistant-approval-rejected",
                ["message"] = "The user rejected this action. Do not retry it; explain the outcome briefly.",
                ["reason"] = request.Reason
            };
        }

        var messages = BuildHistory(history, agent, null);
        messages.Add(new ChatMessage(
            ChatRole.Assistant,
            [new FunctionCallContent(invocation.CallId, invocation.FunctionName, NhAssistantContent.DeserializeArguments(invocation.ArgumentsJson))]));
        messages.Add(new ChatMessage(
            ChatRole.Tool,
            [new FunctionResultContent(invocation.CallId, functionResult)]));
        await RunModelAsync(state, request.CallerContext, messages, cancellationToken);
    }

    private async Task<NhAssistantToolExecution?> ExecuteApprovedAsync(
        NhAssistantTurnState state,
        NhAssistantToolCallInterceptor interceptor,
        AssistantApproval approval,
        AssistantToolInvocation invocation,
        CancellationToken cancellationToken)
    {
        var function = FindGovernedFunction(invocation);
        if (function is null)
        {
            invocation.Status = NhAssistantToolCallStatuses.Failed;
            invocation.ResultCode = NhAiToolFailureCodes.ApprovalInvalid;
            invocation.CompletedAt = DateTimeOffset.UtcNow;
            await store.UpdateToolInvocationAsync(invocation, CancellationToken.None);
            await FinishWithErrorAsync(state, NhAssistantErrorCodes.ApprovalInvalid, NhAssistantConversationStatuses.Idle);
            return null;
        }

        var (aiFunction, descriptor) = function.Value;
        invocation.Status = NhAssistantToolCallStatuses.Running;
        await store.UpdateToolInvocationAsync(invocation, CancellationToken.None);
        // The paused message already references this tool call; its row carries the new status.
        state.ToolCalls++;
        await state.EmitAsync(new NhAssistantToolStartedEvent(
            invocation.Id,
            invocation.ToolId,
            invocation.ToolVersion,
            invocation.DisplayName,
            NhAssistantContent.Preview(invocation.ArgumentsJson, invocation.DataClassification)));

        using var turnScope = NhAssistantExecutionScope.EnterTurn(state.Scope);
        state.Scope.IncrementToolCalls();
        var arguments = new AIFunctionArguments(NhAssistantContent.DeserializeArguments(invocation.ArgumentsJson))
        {
            Services = services
        };
        var execution = await NhAssistantToolCallInterceptor.ExecuteAsync(
            descriptor,
            invocation,
            approval.ProposalId,
            approval.Id,
            token => aiFunction.InvokeAsync(arguments, token),
            cancellationToken);
        await interceptor.CompleteToolCallAsync(invocation, execution, CancellationToken.None);
        return execution;
    }

    private (AIFunction Function, NhAiToolDescriptor Descriptor)? FindGovernedFunction(AssistantToolInvocation invocation)
    {
        foreach (var catalog in _catalogs)
        {
            var descriptors = catalog.Descriptors;
            if (!descriptors.Any(descriptor => Matches(descriptor, invocation)))
            {
                continue;
            }
            foreach (var function in catalog.CreateFunctions(services))
            {
                if (function is INhAiGovernedAIFunction governed && Matches(governed.Descriptor, invocation))
                {
                    return (function, governed.Descriptor);
                }
            }
        }
        return null;
    }

    private static bool Matches(NhAiToolDescriptor descriptor, AssistantToolInvocation invocation)
    {
        return string.Equals(descriptor.Id, invocation.ToolId, StringComparison.Ordinal)
            && descriptor.Version == invocation.ToolVersion
            && string.Equals(descriptor.ContractHash, invocation.ContractHash, StringComparison.Ordinal);
    }

    private async Task RunModelAsync(
        NhAssistantTurnState state,
        NhAiInvocationContext callerContext,
        List<ChatMessage> messages,
        CancellationToken cancellationToken)
    {
        var interceptor = CreateInterceptor(state);
        var outcomeStatus = NhAssistantTurnStatuses.Completed;
        string? errorCode = null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(state.Scope.Limits.TurnTimeout);
        try
        {
            using var turnScope = NhAssistantExecutionScope.EnterTurn(state.Scope);
            var agent = await CreateAgentAsync(state, callerContext, interceptor, timeout.Token);
            if (agent is null)
            {
                outcomeStatus = NhAssistantTurnStatuses.Failed;
                errorCode = NhAssistantErrorCodes.ModelUnavailable;
            }
            else
            {
                await foreach (var update in agent.RunStreamingAsync(messages, null, null, timeout.Token))
                {
                    foreach (var usage in update.Contents.OfType<UsageContent>())
                    {
                        state.InputTokens += (int)Math.Clamp(usage.Details.InputTokenCount ?? 0, 0, int.MaxValue);
                        state.OutputTokens += (int)Math.Clamp(usage.Details.OutputTokenCount ?? 0, 0, int.MaxValue);
                    }
                    var delta = update.Text;
                    if (!string.IsNullOrEmpty(delta) && update.Role != ChatRole.Tool)
                    {
                        state.AppendText(delta);
                        await state.EmitAsync(new NhAssistantMessageDeltaEvent(state.AssistantMessageId, delta));
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            if (cancellations.WasCancelled(state.Conversation.Id, state.Scope.TurnId)
                || cancellationToken.IsCancellationRequested)
            {
                outcomeStatus = NhAssistantTurnStatuses.Cancelled;
            }
            else
            {
                outcomeStatus = NhAssistantTurnStatuses.Failed;
                errorCode = NhAssistantErrorCodes.TurnTimeout;
            }
        }
        catch (InvalidOperationException exception) when (state.Scope.BudgetExhausted)
        {
            logger.LogInformation(
                "Assistant turn {TurnId} stopped because the daily budget is exhausted ({ExceptionType}).",
                state.Scope.TurnId,
                exception.GetType().Name);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Model and provider exceptions can carry content; log the type only.
            logger.LogWarning(
                "Assistant turn {TurnId} of agent {AgentId} failed with {ExceptionType}.",
                state.Scope.TurnId,
                state.Scope.Agent.Id,
                exception.GetType().Name);
            outcomeStatus = NhAssistantTurnStatuses.Failed;
            errorCode = exception is InvalidOperationException
                ? NhAssistantErrorCodes.ModelUnavailable
                : NhAssistantErrorCodes.TurnFailed;
        }

        await interceptor.FlushAsync(CancellationToken.None);
        if (state.Scope.BudgetExhausted && state.PendingApproval is null)
        {
            await FinishWithErrorAsync(state, NhAssistantErrorCodes.BudgetExhausted, NhAssistantConversationStatuses.Idle);
            return;
        }

        string conversationStatus;
        if (outcomeStatus == NhAssistantTurnStatuses.Completed && state.PendingApproval is not null)
        {
            outcomeStatus = NhAssistantTurnStatuses.WaitingForApproval;
            conversationStatus = NhAssistantConversationStatuses.WaitingForApproval;
        }
        else if (outcomeStatus == NhAssistantTurnStatuses.Failed)
        {
            conversationStatus = NhAssistantConversationStatuses.Failed;
        }
        else
        {
            conversationStatus = NhAssistantConversationStatuses.Idle;
            if (outcomeStatus == NhAssistantTurnStatuses.Completed && state.ToolCallLimitReached)
            {
                errorCode = NhAssistantErrorCodes.ToolCallLimitReached;
            }
        }

        await store.TryEndTurnAsync(state.Conversation.Id, state.Scope.TurnId, conversationStatus, CancellationToken.None);
        logger.LogInformation(
            "Assistant turn {TurnId} of agent {AgentId} ended with {Status} after {ToolCalls} tool calls.",
            state.Scope.TurnId,
            state.Scope.Agent.Id,
            outcomeStatus,
            state.ToolCalls);
        await state.EmitAsync(new NhAssistantTurnCompletedEvent(
            state.Scope.TurnId,
            outcomeStatus,
            new NhAssistantTurnUsage(state.InputTokens, state.OutputTokens, state.ToolCalls),
            errorCode));
    }

    private async Task<AIAgent?> CreateAgentAsync(
        NhAssistantTurnState state,
        NhAiInvocationContext callerContext,
        NhAssistantToolCallInterceptor interceptor,
        CancellationToken cancellationToken)
    {
        var agent = state.Scope.Agent;
        if (!profiles.TryGet(agent.ProfileName, out var profile))
        {
            return null;
        }

        var context = callerContext with
        {
            InvocationId = Guid.NewGuid(),
            ActorId = agent.ActorId,
            ActorKind = NhAiActorKind.Agent,
            AccountableOwnerId = state.Scope.OwnerActorId,
            Purpose = NhAssistantInvocationGate.Purpose,
            RunId = state.Scope.TurnId.ToString(),
            CorrelationId = callerContext.CorrelationId ?? state.Scope.TurnId.ToString(),
            AgentVersion = agent.Version.ToString(CultureInfo.InvariantCulture),
            ModelProfileName = agent.ProfileName,
            PromptVersion = state.Scope.PromptVersion,
            PromptHash = state.Scope.PromptHash,
            Deadline = state.Scope.Deadline,
            IdempotencyKey = null,
            ProposalId = null,
            ApprovalId = null,
            RemainingBudget = null
        };
        // Agent Framework and Microsoft.Extensions.AI trace function arguments and results at trace
        // level. The assistant pipeline gets no logger factory so conversation content never reaches logs.
        var agentServices = new NhAssistantContentSafeServiceProvider(services);
        var selectors = await SelectToolsAsync(agent, context, cancellationToken);
        var descriptor = NhAssistantAgentRegistry.CreateDescriptor(agent, profile, selectors);
        var created = await adapter.CreateAsync(
            new NhAiAgentCreateRequest(descriptor, context, agent.Instructions.Content, registration.ExecutionRegion),
            agentServices,
            cancellationToken);
        if (!created.Success)
        {
            logger.LogWarning(
                "Assistant agent {AgentId} could not be created for turn {TurnId} ({FailureCode}).",
                agent.Id,
                state.Scope.TurnId,
                created.GetResultItems().Select(item => item.Name).FirstOrDefault(name => NhAssistantNames.IsSegment(name)) ?? "unclassified");
            return null;
        }

        interceptor.UseOfferedTools(created.Data.Tools);
        return created.Data.Agent
            .AsBuilder()
            .Use(interceptor.InvokeAsync)
            .Build(agentServices);
    }

    /// <summary>
    /// Discovers the agent-exposed tools the caller may see, intersects them with the agent's
    /// selectors and caps the result. Only the count is logged.
    /// </summary>
    private async Task<IReadOnlyList<string>> SelectToolsAsync(
        NhAssistantAgentDefinition agent,
        NhAiInvocationContext context,
        CancellationToken cancellationToken)
    {
        var discovered = await discovery.DiscoverAsync(
            new NhAiToolDiscoveryRequest(context, NhAiToolExposure.Agent),
            cancellationToken);
        var selected = discovered
            .Where(descriptor => agent.ToolSelectors.Any(selector => IsSelected(selector, descriptor.Id)))
            .Where(descriptor => descriptor.Effect == NhAiToolEffect.ReadOnly || agent.CanMutate)
            .Select(descriptor => descriptor.Id)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var limit = registration.Limits.MaxToolsPerAgent;
        if (selected.Length > limit)
        {
            logger.LogWarning(
                "Assistant agent {AgentId} can see {ToolCount} tools; the first {ToolLimit} are offered.",
                agent.Id,
                selected.Length,
                limit);
            selected = selected.Take(limit).ToArray();
        }
        logger.LogDebug("Assistant agent {AgentId} offers {ToolCount} tools.", agent.Id, selected.Length);
        return selected.Length == 0 ? agent.ToolSelectors : selected;
    }

    private static bool IsSelected(string selector, string toolId)
    {
        return string.Equals(selector, toolId, StringComparison.Ordinal)
            || (selector.EndsWith(".*", StringComparison.Ordinal)
                && toolId.StartsWith(selector[..^1], StringComparison.Ordinal));
    }

    private async Task<NhAssistantTurnState> CreateStateAsync(
        AssistantConversation conversation,
        NhAssistantAgentDefinition agent,
        Guid turnId,
        Guid userMessageId,
        NhAiInvocationContext callerContext,
        ChannelWriter<NhAssistantTurnEvent> events)
    {
        var assistantMessage = new AssistantMessage
        {
            Id = Guid.NewGuid(),
            ConversationId = conversation.Id,
            TurnId = turnId,
            Role = NhAssistantMessageRoles.Assistant,
            PartsVersion = NhAssistantContent.PartsVersion,
            PartsJson = "[]",
            CreatedAt = DateTimeOffset.UtcNow
        };
        await store.AddMessageAsync(assistantMessage, CancellationToken.None);
        var limits = registration.Limits;
        return new NhAssistantTurnState
        {
            Scope = new NhAssistantTurnScope
            {
                ConversationId = conversation.Id,
                TurnId = turnId,
                Agent = agent,
                OwnerActorId = callerContext.ActorId,
                TenantId = callerContext.TenantId,
                ModelProfileName = agent.ProfileName,
                PromptVersion = NhAssistantAgentRegistry.PromptVersion(agent),
                PromptHash = agent.Instructions.Manifest.ContentHash,
                Deadline = DateTimeOffset.UtcNow.Add(limits.TurnTimeout),
                Limits = limits
            },
            Conversation = conversation,
            UserMessageId = userMessageId,
            AssistantMessageId = assistantMessage.Id,
            Events = events
        };
    }

    private NhAssistantToolCallInterceptor CreateInterceptor(NhAssistantTurnState state)
    {
        return new NhAssistantToolCallInterceptor(state, store, proposalFactory, _businessSinks);
    }

    private async Task FinishWithErrorAsync(
        NhAssistantTurnState state,
        string code,
        string conversationStatus)
    {
        await CreateInterceptor(state).FlushAsync(CancellationToken.None);
        await store.TryEndTurnAsync(state.Conversation.Id, state.Scope.TurnId, conversationStatus, CancellationToken.None);
        logger.LogInformation(
            "Assistant turn {TurnId} of agent {AgentId} ended with {ErrorCode}.",
            state.Scope.TurnId,
            state.Scope.Agent.Id,
            code);
        await state.EmitAsync(new NhAssistantErrorEvent(code));
    }

    private async Task GenerateTitleAsync(
        AssistantConversation conversation,
        NhAssistantAgentDefinition agent,
        string text)
    {
        if (conversation.Title is not null)
        {
            return;
        }
        var title = await titleGenerator.GenerateAsync(agent.Id, text, CancellationToken.None);
        if (!string.IsNullOrWhiteSpace(title))
        {
            await store.SetTitleIfEmptyAsync(conversation.Id, title.Trim(), CancellationToken.None);
        }
    }

    /// <summary>
    /// Replays persisted user and assistant text, newest first until the model profile's input
    /// budget is used, leaving room for the instructions and the new message.
    /// </summary>
    private List<ChatMessage> BuildHistory(
        IReadOnlyList<AssistantMessage> history,
        NhAssistantAgentDefinition agent,
        string? newMessage)
    {
        var budget = profiles.TryGet(agent.ProfileName, out var profile)
            ? (long)profile.Budget.MaxInputTokens * 3
            : 16_000;
        budget -= agent.Instructions.Content.Length + (newMessage?.Length ?? 0);
        var selected = new List<ChatMessage>();
        foreach (var message in history.Reverse())
        {
            var text = NhAssistantContent.TextOf(NhAssistantContent.DeserializeParts(message.PartsJson));
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }
            budget -= text.Length;
            if (budget < 0)
            {
                break;
            }
            var role = message.Role == NhAssistantMessageRoles.User ? ChatRole.User : ChatRole.Assistant;
            selected.Add(new ChatMessage(role, text));
        }
        selected.Reverse();
        return selected;
    }

    private static NhAssistantAuditEvent DecisionEvent(
        NhAssistantTurnState state,
        AssistantApproval approval,
        AssistantToolInvocation invocation,
        NhAssistantAuditEventKind kind,
        string code,
        DateTimeOffset occurredAt)
    {
        return new NhAssistantAuditEvent(
            kind,
            state.Conversation.Id,
            state.Scope.TurnId,
            state.Scope.OwnerActorId,
            state.Scope.Agent.Id,
            state.Scope.Agent.Version,
            occurredAt)
        {
            ToolId = invocation.ToolId,
            ToolVersion = invocation.ToolVersion,
            InvocationId = invocation.Id,
            ResultCode = code,
            ApprovalId = approval.Id,
            ProposalId = approval.ProposalId,
            StartedAt = invocation.StartedAt
        };
    }

    private static DateTimeOffset StaleBefore(NhAssistantLimits limits)
    {
        return DateTimeOffset.UtcNow - limits.TurnTimeout - TimeSpan.FromMinutes(1);
    }

    private static TaskResult<NhAssistantTurnHandle> Failed(string code, string message)
    {
        return TaskResult<NhAssistantTurnHandle>.Failed(code, message);
    }
}

/// <summary>
/// Service provider for the agent pipeline that withholds the logger factory. Framework components
/// in that pipeline log prompts, function arguments and results at trace level; the assistant keeps
/// conversation content out of logs, so they receive <see cref="Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory"/>.
/// </summary>
internal sealed class NhAssistantContentSafeServiceProvider(IServiceProvider inner) : IServiceProvider
{
    public object? GetService(Type serviceType)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        if (serviceType == typeof(ILoggerFactory))
        {
            return Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance;
        }
        if (serviceType.IsGenericType && serviceType.GetGenericTypeDefinition() == typeof(ILogger<>))
        {
            return Activator.CreateInstance(
                typeof(Microsoft.Extensions.Logging.Abstractions.NullLogger<>).MakeGenericType(serviceType.GetGenericArguments()));
        }
        return inner.GetService(serviceType);
    }
}
