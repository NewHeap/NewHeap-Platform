using System.Threading.Channels;
using Microsoft.Extensions.AI;
using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.AI.Chat.Runs;
using NewHeap.Platform.Common.Models;

namespace NewHeap.Platform.AI.Chat.Runtime;

/// <summary>
/// Structured questions in a person's chat (LS21): <c>ask_question</c> posts a question with choices to
/// a person of the conversation, the turn waits for the answer as for an approval, and only the
/// addressee's answer resumes it, with the answer as the call's result.
/// </summary>
internal sealed partial class NhAssistantTurnRunner
{
    /// <summary>
    /// Whether the turn offers <c>ask_question</c>: a person's chat with an agent that allows questions.
    /// Runs, people's turns in agent threads, rule turns and consultations never offer it.
    /// </summary>
    private static bool OffersQuestions(NhAssistantTurnState state)
    {
        return state.AgentTurn is null
            && state.ChatOptions is { AllowQuestions: true }
            && state.Scope.Consultation is null
            && !state.RuleTurn;
    }

    /// <summary>
    /// <c>ask_question</c>: checks the question and leaves it to the interceptor, which records it with
    /// the call and ends the model loop so the turn waits for the answer.
    /// </summary>
    private static NhAssistantMemoryFunction CreateAskQuestionFunction(NhAssistantTurnState state)
    {
        return new NhAssistantMemoryFunction(
            NhAssistantMemoryFunction.AskQuestionDescriptor,
            NhAssistantQuestions.DescriptionOf(state.Speakers),
            NhAssistantQuestions.SchemaOf(state.Speakers),
            (arguments, _) =>
            {
                if (state.WaitsForPerson || state.AskedQuestion is not null)
                {
                    return ValueTask.FromResult<object?>(
                        NhAssistantQuestions.Refused("Nothing was asked: this turn already waits for a person; ask one question at a time."));
                }

                var (asked, refusal) = NhAssistantQuestions.Check(arguments, state.Speakers, state.Scope.OwnerActorId);
                if (asked is null)
                {
                    return ValueTask.FromResult<object?>(NhAssistantQuestions.Refused(refusal!));
                }

                state.AskedQuestion = asked;
                return ValueTask.FromResult<object?>(NhAssistantQuestions.WaitingResult());
            });
    }

    public async Task<TaskResult<NhAssistantTurnHandle>> StartAnswerTurnAsync(
        NhAssistantAnswerTurnRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var actorId = request.CallerContext.ActorId;
        if (request.ChoiceIndex < 0)
        {
            return Failed(NhAssistantErrorCodes.QuestionAnswerInvalid, "The answer names no choice of the question.");
        }

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
        if (await FindQuestionAsync(conversation, request.QuestionId, cancellationToken) is not { } found)
        {
            return Failed(NhAssistantErrorCodes.QuestionNotFound, "The question was not found.");
        }

        var (invocation, part, waitingParts) = found;
        if (!string.Equals(part.AddresseeActorId, actorId, StringComparison.Ordinal))
        {
            // Only the addressee answers: anyone else would steer the turn with an answer that was not
            // theirs to give.
            return Failed(NhAssistantErrorCodes.QuestionForbidden, "The question is addressed to another person of the conversation.");
        }
        if (invocation.Status != NhAssistantToolCallStatuses.AwaitingAnswer
            || conversation.Status != NhAssistantConversationStatuses.WaitingForApproval
            || conversation.ActiveTurnId != invocation.TurnId)
        {
            return Failed(NhAssistantErrorCodes.QuestionNotPending, "The question no longer waits for an answer.");
        }
        if (request.ChoiceIndex >= part.Choices!.Count)
        {
            return Failed(NhAssistantErrorCodes.QuestionAnswerInvalid, "The answer names no choice of the question.");
        }
        if (_applicationClock.GetUtcNow() >= part.ExpiresAt)
        {
            // The wait follows the application's clock, as an approval's lifetime does.
            await ExpireQuestionAsync(conversation, invocation, part, actorId);
            return Failed(NhAssistantErrorCodes.QuestionExpired, "Nobody answered the question in time.");
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

        // The person who answers claims the turn: it continues with their rights, as every turn runs
        // with the rights of the person whose request carries it.
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
            request.Person,
            (events, token) => RunAnswerTurnAsync(access, effectiveAgent, invocation, part, waitingParts, request, events, token));
    }

    /// <summary>
    /// The question of <paramref name="conversation"/> with <paramref name="questionId"/>: its call, its
    /// part and the parts of the agent's message that asked it; <see langword="null"/> when the
    /// conversation asked no such question.
    /// </summary>
    private async Task<(AssistantToolInvocation Invocation, NhAssistantStoredPart Part, IReadOnlyList<NhAssistantStoredPart> MessageParts)?> FindQuestionAsync(
        AssistantConversation conversation,
        Guid questionId,
        CancellationToken cancellationToken)
    {
        var invocation = await store.FindToolInvocationAsync(questionId, cancellationToken);
        if (invocation is null
            || invocation.ConversationId != conversation.Id
            || !string.Equals(invocation.ToolId, NhAssistantMemoryFunction.AskQuestionDescriptor.Id, StringComparison.Ordinal))
        {
            return null;
        }

        var message = await store.FindMessageAsync(conversation.Id, invocation.MessageId, cancellationToken);
        if (message is null)
        {
            return null;
        }

        var parts = NhAssistantContent.DeserializeParts(message.PartsJson);
        var part = parts.FirstOrDefault(item => item is
        {
            Type: NhAssistantStoredPart.QuestionType,
            Choices: not null,
            AddresseeActorId: not null,
            ExpiresAt: not null
        } && item.InvocationId == invocation.Id);
        return part is null ? null : (invocation, part, parts);
    }

    /// <summary>
    /// Resumes the turn that waits for the question with the answer of its addressee, who carries the
    /// rest of the turn: the call ends with the answer as its result, the people of the conversation
    /// see the answer, the turn continues in the skill it worked in when it asked, and the model
    /// replays the call with the answer.
    /// </summary>
    private async Task RunAnswerTurnAsync(
        NhAssistantConversationAccess access,
        NhAssistantAgent effectiveAgent,
        AssistantToolInvocation invocation,
        NhAssistantStoredPart part,
        IReadOnlyList<NhAssistantStoredPart> waitingParts,
        NhAssistantAnswerTurnRequest request,
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
        var state = await CreateStateAsync(conversation, effectiveAgent, invocation.TurnId, userMessageId, request.CallerContext, request.Language, clientContext, speakers, events);
        state.Scope.Channel = request.Channel;
        var interceptor = CreateInterceptor(state);
        await state.EmitAsync(new NhAssistantTurnStartedEvent(invocation.TurnId, userMessageId, state.AssistantMessageId));

        var question = NhAssistantQuestions.ViewOf(part, invocation, _applicationClock.GetUtcNow())
            ?? throw new InvalidOperationException("A waiting question has no view.");
        var result = NhAssistantQuestions.AnswerResult(question, request.ChoiceIndex, speakers?.NameOf(access.ActorId));
        var resultJson = NhAssistantContent.SerializeResult(result);
        var completedAt = DateTimeOffset.UtcNow;
        if (!await store.TryCloseQuestionAsync(invocation.Id, NhAssistantToolCallStatuses.Succeeded, null, resultJson, completedAt, CancellationToken.None))
        {
            // The question closed in the meantime, for example because its turn was stopped.
            await FinishWithErrorAsync(state, NhAssistantErrorCodes.QuestionNotPending, NhAssistantConversationStatuses.Idle);
            return;
        }

        await state.EmitAsync(new NhAssistantToolCompletedEvent(
            invocation.Id,
            NhAssistantToolCallStatuses.Succeeded,
            null,
            NhAssistantContent.ResultPreview(resultJson, invocation.DataClassification)));
        await state.EmitAsync(new NhAssistantQuestionAnsweredEvent(question with
        {
            Status = NhAssistantQuestionStatuses.Answered,
            AnswerIndex = request.ChoiceIndex,
            ClosedAt = completedAt
        }));
        await interceptor.RecordBusinessEventAsync(
            QuestionEvent(state.Conversation, NhAssistantAuditEventKind.QuestionAnswered, invocation, part, access.ActorId, NhAssistantQuestions.AnsweredCode) with
            {
                AgentVersion = state.Scope.Agent.Version,
                CompletedAt = completedAt
            },
            CancellationToken.None);

        // The turn offers its skills and questions again and continues in the skill it asked from.
        await PrepareSkillsAsync(state, cancellationToken);
        if (SkillAskedFrom(state, waitingParts, part) is { } skill)
        {
            await EnterSkillAsync(state, skill.Skill, skill.ChosenBy, request.CallerContext, record: true, cancellationToken);
        }

        // The resumed call is replayed below with its answer; the summary leaves it out.
        var messages = await BuildHistoryAsync(conversation.Id, history, agent, null, invocation.Id, speakers, state: state);
        messages.Add(new ChatMessage(
            ChatRole.Assistant,
            [new FunctionCallContent(invocation.CallId, invocation.FunctionName, NhAssistantContent.DeserializeArguments(invocation.ArgumentsJson))]));
        messages.Add(new ChatMessage(
            ChatRole.Tool,
            [new FunctionResultContent(invocation.CallId, result)]));
        await RunModelAsync(state, request.CallerContext, messages, cancellationToken);
    }

    /// <summary>
    /// The skill the turn worked in when it asked: the last skill its message recorded before the
    /// question, while that skill is still active and live; <see langword="null"/> for the agent itself.
    /// </summary>
    private static (NhAssistantSkillOptions Skill, string ChosenBy)? SkillAskedFrom(
        NhAssistantTurnState state,
        IReadOnlyList<NhAssistantStoredPart> waitingParts,
        NhAssistantStoredPart question)
    {
        var recorded = waitingParts
            .TakeWhile(part => !ReferenceEquals(part, question))
            .LastOrDefault(part => part is { Type: NhAssistantStoredPart.SkillType, SkillId: not null });
        if (recorded is null)
        {
            return null;
        }

        var skill = state.ActiveSkills.FirstOrDefault(item => item.Id == recorded.SkillId && !state.ShadowSkills.Contains(item.Id));
        return skill is null ? null : (skill, recorded.Reason ?? NhAssistantSkillChoosers.Model);
    }

    /// <summary>
    /// Closes the question the stopped turn waited for, so it reads as cancelled.
    /// </summary>
    private async Task CancelWaitingQuestionAsync(AssistantConversation conversation, CancellationToken cancellationToken)
    {
        if (conversation.ActiveTurnId is not { } turnId)
        {
            return;
        }

        var waiting = await store.FindAwaitingQuestionAsync(conversation.Id, turnId, cancellationToken);
        if (waiting is null)
        {
            return;
        }

        await store.TryCloseQuestionAsync(
            waiting.Id,
            NhAssistantToolCallStatuses.NotExecuted,
            NhAssistantErrorCodes.QuestionCancelled,
            NhAssistantContent.SerializeResult(NhAssistantQuestions.Closed(
                NhAssistantErrorCodes.QuestionCancelled,
                "The turn was stopped before the question was answered.")),
            DateTimeOffset.UtcNow,
            CancellationToken.None);
    }

    /// <summary>
    /// Closes a question that nobody answered within its wait and releases the waiting turn, so the
    /// conversation takes the next message.
    /// </summary>
    private async Task ExpireQuestionAsync(
        AssistantConversation conversation,
        AssistantToolInvocation invocation,
        NhAssistantStoredPart part,
        string actorId)
    {
        var expired = await store.TryCloseQuestionAsync(
            invocation.Id,
            NhAssistantToolCallStatuses.Failed,
            NhAssistantErrorCodes.QuestionExpired,
            NhAssistantContent.SerializeResult(NhAssistantQuestions.Closed(
                NhAssistantErrorCodes.QuestionExpired,
                "Nobody answered the question in time.")),
            DateTimeOffset.UtcNow,
            CancellationToken.None);
        if (expired)
        {
            var evt = QuestionEvent(conversation, NhAssistantAuditEventKind.QuestionExpired, invocation, part, actorId, NhAssistantErrorCodes.QuestionExpired);
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
    /// The content-free audit event of a question: its call, its addressee and the outcome code.
    /// </summary>
    private static NhAssistantAuditEvent QuestionEvent(
        AssistantConversation conversation,
        NhAssistantAuditEventKind kind,
        AssistantToolInvocation invocation,
        NhAssistantStoredPart part,
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
            ObjectId = part.AddresseeActorId,
            ResultCode = resultCode,
            StartedAt = invocation.StartedAt
        };
    }
}
