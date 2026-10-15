using Microsoft.Extensions.Logging;
using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Chat.Notifications;
using NewHeap.Platform.AI.Chat.Persistence;

namespace NewHeap.Platform.AI.Chat.Runs;

/// <summary>
/// Tells people outside a thread about agent events: it reads the subject's member snapshot,
/// asks the notification policy for recipients and hands them to Web Push and every registered
/// channel. Shadow runs and shadow agents never notify. A failed notification never fails a run.
/// </summary>
internal sealed class NhAssistantAgentNotifier(
    NhAssistantRunStore runs,
    NhAssistantSubjectAccessService subjects,
    NhAssistantRegistrationState registration,
    INhAssistantAgentNotificationPolicy policy,
    IEnumerable<INhAssistantAgentNotificationChannel> channels,
    INhAssistantNotifier pushNotifier,
    ILogger<NhAssistantAgentNotifier> logger)
{
    private readonly IReadOnlyList<INhAssistantAgentNotificationChannel> _channels = channels.ToArray();

    public async Task NotifyAsync(
        NhAssistantAgentNotificationKind kind,
        AssistantThread thread,
        Guid? runId,
        AssistantApproval? approval,
        string? initiatorActorId,
        bool shadow,
        CancellationToken cancellationToken,
        int proposalCount = 0,
        string? failureCode = null)
    {
        if (shadow
            || approval?.Status == NhAssistantApprovalStatuses.Shadow
            || (registration.AgentRuns.TryGetValue(thread.AgentId, out var options) && options.Shadow))
        {
            return;
        }

        try
        {
            var subject = new NhAiSubjectRef(thread.SubjectType, thread.SubjectId);
            if (thread.MembersRefreshedAt is null)
            {
                // The first notification of a thread fills its member snapshot.
                await subjects.RefreshAsync(subject, cancellationToken);
            }

            var members = (await runs.GetMembersAsync(thread.ScopeKey, subject, cancellationToken))
                .Select(member => new NhAiSubjectMember(member.ActorId, member.Role) { DisplayName = member.DisplayName })
                .ToArray();
            var context = new NhAssistantAgentNotificationContext(
                kind,
                thread.AgentId,
                thread.ConversationId,
                subject,
                thread.SubjectDisplayName,
                runId,
                approval?.Id,
                initiatorActorId,
                members)
            {
                ProposalCount = proposalCount,
                ToolId = approval?.ToolId,
                ExpiresAt = approval?.ExpiresAt,
                FailureCode = failureCode
            };
            var recipients = (await policy.SelectRecipientsAsync(context, cancellationToken))
                .Where(actorId => !string.IsNullOrWhiteSpace(actorId) && actorId.Length <= 256)
                .Where(actorId => !actorId.StartsWith(NhAssistantAgentEvents.AgentActorPrefix, StringComparison.Ordinal))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (recipients.Length == 0)
            {
                return;
            }

            await pushNotifier.AgentNotificationAsync(context, recipients, cancellationToken);
            foreach (var channel in _channels)
            {
                try
                {
                    await channel.DeliverAsync(context, recipients, cancellationToken);
                }
                catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
                {
                    logger.LogWarning(
                        "Agent notification channel {Channel} failed for {Kind} ({ExceptionType}).",
                        channel.GetType().Name,
                        kind,
                        exception.GetType().Name);
                }
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
        {
            logger.LogWarning(
                "Agent notification {Kind} for thread {ConversationId} was not sent ({ExceptionType}).",
                kind,
                thread.ConversationId,
                exception.GetType().Name);
        }
    }
}

/// <summary>
/// Records content-free business audit events of agent runs: accepted and rejected triggers,
/// ended runs, executed and resolved proposals, proposal feedback and its export, and kill-switch
/// use.
/// </summary>
internal sealed class NhAssistantAgentEvents(
    IEnumerable<INhAssistantBusinessAuditSink> businessSinks,
    ILogger<NhAssistantAgentEvents> logger)
{
    internal const string AgentActorPrefix = "assistant-agent:";

    private readonly IReadOnlyList<INhAssistantBusinessAuditSink> _businessSinks = businessSinks.ToArray();

    /// <param name="subject">The trigger's subject; <see langword="null"/> when the trigger was
    /// refused before its subject was read.</param>
    public Task TriggerAsync(
        string agentId,
        Guid? conversationId,
        string triggerId,
        Guid? runId,
        NhAiSubjectRef? subject,
        string? initiatorActorId,
        string outcome,
        bool accepted,
        CancellationToken cancellationToken,
        string? skillId = null)
    {
        return RecordAsync(
            new NhAssistantAuditEvent(
                accepted ? NhAssistantAuditEventKind.AgentTriggerAccepted : NhAssistantAuditEventKind.AgentTriggerRejected,
                conversationId ?? Guid.Empty,
                Guid.Empty,
                initiatorActorId ?? AgentActorPrefix + agentId,
                agentId,
                0,
                DateTimeOffset.UtcNow)
            {
                ResultCode = outcome,
                RunId = runId,
                TriggerId = triggerId,
                SubjectType = subject?.Type,
                SubjectId = subject?.Id,
                InitiatorActorId = initiatorActorId,
                SkillId = skillId
            },
            cancellationToken);
    }

    public Task RunEndedAsync(AssistantThread thread, AssistantRun run, string status, string? failureCode, CancellationToken cancellationToken)
    {
        return RecordAsync(
            new NhAssistantAuditEvent(
                NhAssistantAuditEventKind.AgentRunEnded,
                run.ConversationId,
                run.TurnId ?? Guid.Empty,
                AgentActorPrefix + thread.AgentId,
                thread.AgentId,
                run.AgentVersion,
                DateTimeOffset.UtcNow)
            {
                ResultCode = failureCode is null ? status : status + ":" + failureCode,
                RunId = run.Id,
                TriggerId = run.TriggerId,
                SubjectType = thread.SubjectType,
                SubjectId = thread.SubjectId,
                InitiatorActorId = run.InitiatorActorId,
                SkillId = run.SkillId
            },
            cancellationToken);
    }

    public Task ProposalAsync(
        NhAssistantAuditEventKind kind,
        AssistantThread thread,
        AssistantApproval approval,
        string actorId,
        string resultCode,
        CancellationToken cancellationToken)
    {
        return RecordAsync(
            new NhAssistantAuditEvent(
                kind,
                approval.ConversationId,
                approval.TurnId,
                actorId,
                thread.AgentId,
                0,
                DateTimeOffset.UtcNow)
            {
                ToolId = approval.ToolId,
                InvocationId = approval.ToolInvocationId,
                ResultCode = resultCode,
                ApprovalId = approval.Id,
                ProposalId = approval.ProposalId,
                RunId = approval.RunId,
                SubjectType = thread.SubjectType,
                SubjectId = thread.SubjectId,
                InitiatorActorId = approval.InitiatorActorId,
                GrantSource = approval.GrantSource,
                ApprovalPolicyId = approval.PolicyId,
                ConsultationId = approval.ConsultationId
            },
            cancellationToken);
    }

    /// <summary>
    /// Records that a guard closed a proposal as stale: the kind and reason code, and the stage in
    /// <c>ObjectId</c>. A proposal of a person's chat outside a subject thread has no thread.
    /// </summary>
    public Task InvalidatedAsync(
        AssistantApproval approval,
        string agentId,
        AssistantThread? thread,
        string actorId,
        string kind,
        string reasonCode,
        string stage,
        CancellationToken cancellationToken)
    {
        return RecordAsync(
            new NhAssistantAuditEvent(
                NhAssistantAuditEventKind.AgentProposalInvalidated,
                approval.ConversationId,
                approval.TurnId,
                actorId,
                agentId,
                0,
                DateTimeOffset.UtcNow)
            {
                ToolId = approval.ToolId,
                InvocationId = approval.ToolInvocationId,
                ResultCode = kind + ":" + reasonCode,
                ObjectId = stage,
                ApprovalId = approval.Id,
                ProposalId = approval.ProposalId,
                RunId = approval.RunId,
                SubjectType = thread?.SubjectType,
                SubjectId = thread?.SubjectId,
                InitiatorActorId = approval.InitiatorActorId,
                GrantSource = approval.GrantSource,
                ApprovalPolicyId = approval.PolicyId,
                ConsultationId = approval.ConsultationId
            },
            cancellationToken);
    }

    /// <summary>
    /// Records that an agent did not record a proposal because <paramref name="existing"/> made it
    /// unnecessary.
    /// </summary>
    public Task DeduplicatedAsync(
        AssistantApproval existing,
        AssistantThread thread,
        string actorId,
        string resultCode,
        CancellationToken cancellationToken)
    {
        return ProposalAsync(
            NhAssistantAuditEventKind.AgentProposalDeduplicated,
            thread,
            existing,
            actorId,
            resultCode,
            cancellationToken);
    }

    /// <summary>
    /// Records that <paramref name="supersededBy"/> replaced the pending <paramref name="approval"/>.
    /// </summary>
    public Task SupersededAsync(
        AssistantApproval approval,
        AssistantThread thread,
        Guid supersededBy,
        string actorId,
        CancellationToken cancellationToken)
    {
        return RecordAsync(
            new NhAssistantAuditEvent(
                NhAssistantAuditEventKind.AgentProposalSuperseded,
                approval.ConversationId,
                approval.TurnId,
                actorId,
                thread.AgentId,
                0,
                DateTimeOffset.UtcNow)
            {
                ToolId = approval.ToolId,
                InvocationId = approval.ToolInvocationId,
                ResultCode = NhAssistantErrorCodes.ProposalSuperseded,
                ObjectId = supersededBy.ToString("D"),
                ApprovalId = approval.Id,
                ProposalId = approval.ProposalId,
                RunId = approval.RunId,
                SubjectType = thread.SubjectType,
                SubjectId = thread.SubjectId,
                InitiatorActorId = approval.InitiatorActorId,
                GrantSource = approval.GrantSource,
                ApprovalPolicyId = approval.PolicyId,
                ConsultationId = approval.ConsultationId
            },
            cancellationToken);
    }

    public Task ConsultationAsync(NhAssistantConsultationEvent consultation, CancellationToken cancellationToken)
    {
        return RecordAsync(
            new NhAssistantAuditEvent(
                NhAssistantAuditEventKind.AgentConsulted,
                consultation.ConversationId,
                consultation.ConsultationId ?? Guid.Empty,
                consultation.AskerActorId,
                consultation.AgentId,
                0,
                DateTimeOffset.UtcNow)
            {
                ResultCode = consultation.ResultCode,
                TriggerId = NhAssistantTriggerIds.Consultation,
                SubjectType = consultation.SubjectType,
                SubjectId = consultation.SubjectId,
                InitiatorActorId = consultation.AskerActorId,
                GrantSource = NhAiGrantSource.Delegated,
                ConsultationId = consultation.ConsultationId,
                ConsultationDepth = consultation.Depth,
                AskingAgentId = consultation.AskingAgentId,
                ToolCallCount = consultation.ToolCalls,
                InputTokens = consultation.InputTokens,
                OutputTokens = consultation.OutputTokens,
                ProposalCount = consultation.ProposalCount
            },
            cancellationToken);
    }

    public Task RequestAsync(
        string agentId,
        NhAiSubjectRef subject,
        string askerActorId,
        string? askingAgentId,
        string outcome,
        Guid? conversationId,
        Guid? runId,
        CancellationToken cancellationToken)
    {
        return RecordAsync(
            new NhAssistantAuditEvent(
                NhAssistantAuditEventKind.AgentRequested,
                conversationId ?? Guid.Empty,
                Guid.Empty,
                askerActorId,
                agentId,
                0,
                DateTimeOffset.UtcNow)
            {
                ResultCode = outcome,
                RunId = runId,
                TriggerId = NhAssistantTriggerIds.AgentRequest,
                SubjectType = subject.Type,
                SubjectId = subject.Id,
                InitiatorActorId = askerActorId,
                AskingAgentId = askingAgentId
            },
            cancellationToken);
    }

    /// <summary>
    /// Records a pause, stop or resume of an agent. The reason stays out of the business audit
    /// sinks, like other free text; the agent's state keeps it.
    /// </summary>
    /// <param name="runState">The new state: active, paused or stopped.</param>
    /// <param name="count">The runs a stop cancelled or a resume started.</param>
    public Task AgentStateChangedAsync(
        string agentId,
        string actorId,
        string runState,
        int count,
        CancellationToken cancellationToken,
        string? skillId = null)
    {
        var number = count.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var (kind, resultCode) = runState switch
        {
            NhAssistantAgentRunStates.Stopped => (NhAssistantAuditEventKind.AdminAgentRunsStopped, "cancelled-" + number),
            NhAssistantAgentRunStates.Paused => (NhAssistantAuditEventKind.AdminAgentPaused, "paused"),
            _ => (NhAssistantAuditEventKind.AdminAgentRunsResumed, "resumed-" + number)
        };
        return RecordAsync(
            new NhAssistantAuditEvent(kind, Guid.Empty, Guid.Empty, actorId, agentId, 0, DateTimeOffset.UtcNow)
            {
                ObjectId = agentId,
                ResultCode = resultCode,
                SkillId = skillId
            },
            cancellationToken);
    }

    public Task FeedbackExportedAsync(string agentId, string actorId, CancellationToken cancellationToken)
    {
        return RecordAsync(
            new NhAssistantAuditEvent(
                NhAssistantAuditEventKind.AdminAgentFeedbackExported,
                Guid.Empty,
                Guid.Empty,
                actorId,
                agentId,
                0,
                DateTimeOffset.UtcNow)
            {
                ObjectId = agentId,
                ResultCode = "exported"
            },
            cancellationToken);
    }

    private async Task RecordAsync(NhAssistantAuditEvent evt, CancellationToken cancellationToken)
    {
        foreach (var sink in _businessSinks)
        {
            try
            {
                await sink.RecordAsync(evt, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
            {
                logger.LogWarning(
                    "Assistant audit sink {Sink} failed for {Kind} ({ExceptionType}).",
                    sink.GetType().Name,
                    evt.Kind,
                    exception.GetType().Name);
            }
        }
    }
}
