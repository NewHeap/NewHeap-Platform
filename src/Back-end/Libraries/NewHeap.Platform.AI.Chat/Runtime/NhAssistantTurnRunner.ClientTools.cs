using System.Threading.Channels;
using Microsoft.Extensions.AI;
using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.Common.Models;

namespace NewHeap.Platform.AI.Chat.Runtime;

/// <summary>
/// Client tools in a person's chat (<see cref="NhAssistantBuilder.AddClientTool{TInput}"/>): the turn hands a
/// call to its person's browser and waits as for a question, and only that person's report resumes it,
/// with the report as the call's result.
/// </summary>
internal sealed partial class NhAssistantTurnRunner
{
    public async Task<TaskResult<NhAssistantTurnHandle>> StartClientToolResultTurnAsync(
        NhAssistantClientToolResultTurnRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var actorId = request.CallerContext.ActorId;
        var access = await store.FindAccessAsync(
            request.ConversationId,
            actorId,
            request.CallerContext.TenantId,
            cancellationToken);
        if (access is null)
        {
            return Failed(NhAssistantErrorCodes.ConversationNotFound, "The assistant conversation was not found.");
        }

        var conversation = access.Conversation;
        if (await FindClientToolAsync(conversation, request.InvocationId, cancellationToken) is not { } found)
        {
            return Failed(NhAssistantErrorCodes.ClientToolNotFound, "The client tool call was not found.");
        }

        var (invocation, part) = found;
        if (!string.Equals(part.AddresseeActorId, actorId, StringComparison.Ordinal))
        {
            // Only the person whose turn called it reports: anyone else would steer the turn with a
            // result their own browser never produced.
            return Failed(NhAssistantErrorCodes.ClientToolForbidden, "The client tool runs in the browser of another person of the conversation.");
        }
        if (invocation.Status != NhAssistantToolCallStatuses.AwaitingClient
            || conversation.Status != NhAssistantConversationStatuses.WaitingForApproval
            || conversation.ActiveTurnId != invocation.TurnId)
        {
            return Failed(NhAssistantErrorCodes.ClientToolNotPending, "The client tool call no longer waits for a result.");
        }
        if (!registration.TryGetClientTool(invocation.ToolId, out var tool))
        {
            // The application no longer declares the tool: nothing can bind its result.
            return Failed(NhAssistantErrorCodes.ClientToolNotFound, "The client tool is no longer declared.");
        }

        var (report, refusal) = NhAssistantClientTools.Check(request.Status, request.Outcome, request.Id, request.Data, tool);
        if (report is null)
        {
            return Failed(NhAssistantErrorCodes.ClientToolResultInvalid, refusal!);
        }
        if (_applicationClock.GetUtcNow() >= part.ExpiresAt)
        {
            // The wait follows the application's clock, as a question's does.
            await ExpireClientToolAsync(conversation, invocation, actorId, null);
            return Failed(NhAssistantErrorCodes.ClientToolExpired, "No result came from the person's browser in time.");
        }

        var effectiveAgent = await agents.FindAsync(conversation.AgentId, includeDisabled: false, cancellationToken);
        if (effectiveAgent is null)
        {
            return Failed(NhAssistantErrorCodes.AgentNotFound, "The assistant agent is no longer available.");
        }
        if (effectiveAgent.InstructionsProblem is not null)
        {
            return Failed(NhAssistantErrorCodes.AgentInstructionsUnavailable, "The assistant agent's instructions are not available.");
        }

        // The report continues the turn: the person needs the chat's subject now, as for a message (FB25).
        var chatSubject = await CheckChatSubjectAsync(conversation, null, effectiveAgent, request.CallerContext, cancellationToken);
        if (!chatSubject.Success)
        {
            return TaskResult<NhAssistantTurnHandle>.Failed(chatSubject);
        }

        var claimed = await store.TryBeginTurnAsync(
            conversation.Id,
            actorId,
            DecisionStartStatuses,
            invocation.TurnId,
            DateTimeOffset.MinValue,
            cancellationToken);
        if (!claimed)
        {
            return Failed(NhAssistantErrorCodes.ConversationBusy, "The assistant conversation already has an active turn.");
        }

        return Start(
            conversation,
            invocation.TurnId,
            request.RequestAborted,
            request.CallerContext,
            request.Person,
            (events, token) => RunClientToolResultTurnAsync(access, effectiveAgent, invocation, report, request, chatSubject.Data, events, token));
    }

    /// <summary>
    /// The client tool call of <paramref name="conversation"/> with <paramref name="invocationId"/> and its
    /// part; <see langword="null"/> when the conversation made no such call.
    /// </summary>
    private async Task<(AssistantToolInvocation Invocation, NhAssistantStoredPart Part)?> FindClientToolAsync(
        AssistantConversation conversation,
        Guid invocationId,
        CancellationToken cancellationToken)
    {
        var invocation = await store.FindToolInvocationAsync(invocationId, cancellationToken);
        if (invocation is null || invocation.ConversationId != conversation.Id)
        {
            return null;
        }

        var message = await store.FindMessageAsync(conversation.Id, invocation.MessageId, cancellationToken);
        if (message is null)
        {
            return null;
        }

        var part = NhAssistantContent.DeserializeParts(message.PartsJson).FirstOrDefault(item => item is
        {
            Type: NhAssistantStoredPart.ClientToolType,
            AddresseeActorId: not null,
            ExpiresAt: not null
        } && item.InvocationId == invocation.Id);
        return part is null ? null : (invocation, part);
    }

    /// <summary>
    /// Resumes the turn that waits for its person's browser with what the browser reported: the call ends
    /// with the report as its result, the turn continues on the allowances it has left and in the skill it
    /// worked in, and the model replays the call with the report.
    /// </summary>
    private async Task RunClientToolResultTurnAsync(
        NhAssistantConversationAccess access,
        NhAssistantAgent effectiveAgent,
        AssistantToolInvocation invocation,
        NhAssistantClientToolReport report,
        NhAssistantClientToolResultTurnRequest request,
        NhAiSubjectInfo? chatSubject,
        ChannelWriter<NhAssistantTurnEvent> events,
        CancellationToken cancellationToken)
    {
        var conversation = access.Conversation;
        var agent = effectiveAgent.Definition;
        await live.PublishChangedAsync(conversation.Id, CancellationToken.None);
        var history = await store.GetMessagesAsync(
            conversation.Id,
            registration.Limits.MaxHistoryMessages,
            CancellationToken.None);
        var userMessage = history
            .LastOrDefault(message => message.TurnId == invocation.TurnId && message.Role == NhAssistantMessageRoles.User);
        var userMessageId = userMessage?.Id ?? Guid.Empty;
        // The resumed turn sees the page the user had open when the message was sent.
        var clientContext = NhAssistantClientContext.FromStorage(userMessage?.ClientContextJson);
        var speakers = await LoadSpeakersAsync(conversation, access.ActorId, request.Language);
        var state = await CreateStateAsync(conversation, effectiveAgent, invocation.TurnId, userMessageId, request.CallerContext, request.Language, clientContext, speakers, events, chatSubject: chatSubject);
        state.ClientTools = request.ClientTools;
        var interceptor = CreateInterceptor(state);
        await state.EmitAsync(new NhAssistantTurnStartedEvent(invocation.TurnId, userMessageId, state.AssistantMessageId));

        var result = NhAssistantClientTools.ResultOf(report);
        var resultJson = NhAssistantContent.SerializeResult(result);
        var (status, code) = NhAssistantClientTools.CallStatusOf(report);
        var completedAt = DateTimeOffset.UtcNow;
        if (!await store.TryCloseClientToolAsync(invocation.Id, status, code, resultJson, completedAt, CancellationToken.None))
        {
            // The call closed in the meantime, for example because its turn was stopped.
            await FinishWithErrorAsync(state, NhAssistantErrorCodes.ClientToolNotPending, NhAssistantConversationStatuses.Idle);
            return;
        }

        await state.EmitAsync(new NhAssistantToolCompletedEvent(
            invocation.Id,
            status,
            code,
            NhAssistantContent.ResultPreview(resultJson, invocation.DataClassification)));
        await interceptor.RecordBusinessEventAsync(
            ClientToolEvent(state.Conversation, NhAssistantAuditEventKind.ClientToolCompleted, invocation, access.ActorId, report.Status) with
            {
                AgentVersion = state.Scope.Agent.Version,
                CompletedAt = completedAt
            },
            CancellationToken.None);

        // The report continues the turn on what is left of its allowances; handing over buys it no room.
        await ContinueAllowancesAsync(state, history, CancellationToken.None);

        // The turn offers its skills and questions again and continues in the skill it called the tool in.
        await PrepareSkillsAsync(state, cancellationToken);
        var waitingParts = history.FirstOrDefault(message => message.Id == invocation.MessageId) is { } waiting
            ? NhAssistantContent.DeserializeParts(waiting.PartsJson)
            : [];
        var calledIn = SkillWorkedIn(
            state,
            waitingParts.TakeWhile(part => !(part.Type == NhAssistantStoredPart.ClientToolType && part.InvocationId == invocation.Id)));
        if (calledIn is { } skill)
        {
            await EnterSkillAsync(state, skill.Skill, skill.ChosenBy, request.CallerContext, record: true, cancellationToken);
        }

        // The resumed call is replayed below with its result; the summary leaves it out.
        using var fileDeadline = BeginFileDeadline(state, cancellationToken);
        var messages = await WithinFileDeadlineAsync(
            state,
            fileDeadline,
            token => BuildHistoryAsync(conversation.Id, history, agent, null, invocation.Id, speakers, 0, state, null, token),
            cancellationToken);
        if (messages is null)
        {
            return;
        }

        messages.Add(new ChatMessage(
            ChatRole.Assistant,
            [new FunctionCallContent(invocation.CallId, invocation.FunctionName, NhAssistantContent.DeserializeArguments(invocation.ArgumentsJson))]));
        messages.Add(new ChatMessage(
            ChatRole.Tool,
            [new FunctionResultContent(invocation.CallId, result)]));
        await RunModelAsync(state, request.CallerContext, messages, cancellationToken);
    }

    /// <summary>
    /// Closes the client tool call the stopped turn waited for, so it reads as cancelled.
    /// </summary>
    private async Task CancelWaitingClientToolAsync(AssistantConversation conversation, CancellationToken cancellationToken)
    {
        if (conversation.ActiveTurnId is not { } turnId)
        {
            return;
        }

        var waiting = await store.FindAwaitingClientToolAsync(conversation.Id, turnId, cancellationToken);
        if (waiting is null)
        {
            return;
        }

        await store.TryCloseClientToolAsync(
            waiting.Id,
            NhAssistantToolCallStatuses.NotExecuted,
            NhAssistantErrorCodes.ClientToolCancelled,
            NhAssistantContent.SerializeResult(NhAssistantClientTools.Closed(
                NhAssistantErrorCodes.ClientToolCancelled,
                "The turn was stopped before the person's app reported what happened.")),
            DateTimeOffset.UtcNow,
            CancellationToken.None);
    }

    /// <summary>
    /// Closes a client tool call whose result never came within its wait and releases the waiting turn, so
    /// the conversation takes the next message. A late result finds it, and so does a new message (CS9).
    /// </summary>
    /// <param name="actorId">The person whose result or message found the call expired.</param>
    /// <param name="channel">The application's channel of that message, which the audit records;
    /// <see langword="null"/> for the endpoints.</param>
    private async Task ExpireClientToolAsync(
        AssistantConversation conversation,
        AssistantToolInvocation invocation,
        string actorId,
        string? channel)
    {
        var expired = await store.TryCloseClientToolAsync(
            invocation.Id,
            NhAssistantToolCallStatuses.Failed,
            NhAssistantErrorCodes.ClientToolExpired,
            NhAssistantContent.SerializeResult(NhAssistantClientTools.Closed(
                NhAssistantErrorCodes.ClientToolExpired,
                "No result came from the person's app in time.")),
            DateTimeOffset.UtcNow,
            CancellationToken.None);
        if (expired)
        {
            var evt = ClientToolEvent(conversation, NhAssistantAuditEventKind.ClientToolExpired, invocation, actorId, NhAssistantErrorCodes.ClientToolExpired) with
            {
                Channel = channel
            };
            foreach (var sink in _businessSinks)
            {
                await sink.RecordAsync(evt, CancellationToken.None);
            }
        }
        if (await store.TryReleaseWaitingTurnAsync(conversation.Id, invocation.TurnId, CancellationToken.None))
        {
            await live.PublishChangedAsync(conversation.Id, CancellationToken.None);
        }
    }

    /// <summary>
    /// The content-free audit event of a client tool call: its tool, its call, its person and the outcome
    /// code, never the arguments or what the browser reported beyond its status.
    /// </summary>
    private static NhAssistantAuditEvent ClientToolEvent(
        AssistantConversation conversation,
        NhAssistantAuditEventKind kind,
        AssistantToolInvocation invocation,
        string actorId,
        string resultCode)
    {
        return new NhAssistantAuditEvent(
            kind,
            conversation.Id,
            invocation.TurnId,
            actorId,
            conversation.AgentId,
            conversation.AgentVersion,
            DateTimeOffset.UtcNow)
        {
            ToolId = invocation.ToolId,
            ToolVersion = invocation.ToolVersion,
            InvocationId = invocation.Id,
            ObjectId = actorId,
            ResultCode = resultCode,
            StartedAt = invocation.StartedAt
        };
    }
}
