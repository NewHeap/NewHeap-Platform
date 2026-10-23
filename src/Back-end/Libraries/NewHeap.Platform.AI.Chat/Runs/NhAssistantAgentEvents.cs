using Microsoft.Extensions.Logging;
using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Chat.Notifications;
using NewHeap.Platform.AI.Chat.Persistence;

namespace NewHeap.Platform.AI.Chat.Runs;

/// <summary>
/// Tells people outside a thread about agent events: it reads the subject's member snapshot,
/// asks the notification policy for recipients and hands them to Web Push and every registered
/// channel. The proposals of a run reach each person once per run (<see cref="NotifyProposalsAsync"/>).
/// Shadow runs and shadow agents never notify. A failed notification never fails a run.
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

    /// <summary>
    /// Tells the person who asked how a consultation proposal that waited at the subject ended (CP16,
    /// CP28): rejected, expired, closed or executed, with the asker's or the agent's rights. A proposal
    /// the asker decided in their own chat and proposals of runs notify nobody here.
    /// </summary>
    public Task ConsultationProposalEndedAsync(
        AssistantThread thread,
        AssistantApproval approval,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(thread);
        ArgumentNullException.ThrowIfNull(approval);
        var route = NhAssistantConsultationRoutes.Of(approval.ConsultationId, approval.Mode, approval.GrantSource);
        if (route is not (NhAssistantConsultationRoutes.SubjectAgentRights or NhAssistantConsultationRoutes.SubjectAskerRights)
            || string.IsNullOrWhiteSpace(approval.InitiatorActorId)
            || !NhAssistantApprovalStatuses.IsClosed(approval.Status))
        {
            return Task.CompletedTask;
        }

        return NotifyAsync(
            NhAssistantAgentNotificationKind.ConsultationProposalDecided,
            thread,
            null,
            approval,
            approval.InitiatorActorId,
            false,
            cancellationToken);
    }

    /// <summary>
    /// Tells the recipients the policy picks about one agent event in <paramref name="thread"/>. The
    /// kinds of waiting, expiring and escalated proposals go through
    /// <see cref="NotifyProposalsAsync"/> with this one object, so the channels receive them bundled
    /// as well.
    /// </summary>
    public async Task NotifyAsync(
        NhAssistantAgentNotificationKind kind,
        AssistantThread thread,
        Guid? runId,
        AssistantApproval? approval,
        string? initiatorActorId,
        bool shadow,
        CancellationToken cancellationToken,
        int proposalCount = 0,
        string? failureCode = null,
        NhAssistantMcpConnectionNotice? mcpConnection = null)
    {
        if (BundledKindOf(kind) is not null)
        {
            await NotifyProposalsAsync(
                kind,
                runId,
                runId is null ? null : thread.ConversationId,
                initiatorActorId,
                shadow,
                [new NhAssistantProposalNotice(thread, approval is null ? [] : [approval], initiatorActorId, Math.Max(proposalCount, 1))],
                cancellationToken);
            return;
        }

        // Shadow work notifies nobody: a shadow run, a shadow proposal, a shadow thread or a variant.
        if (shadow || IsShadow(thread, approval is null ? [] : [approval]))
        {
            return;
        }

        try
        {
            var context = (await ContextAsync(kind, thread, runId, approval, initiatorActorId, cancellationToken)) with
            {
                ProposalCount = proposalCount,
                FailureCode = failureCode,
                McpServerId = mcpConnection?.ServerId,
                McpServerDisplayName = mcpConnection?.ServerDisplayName,
                McpConnectionPersonId = mcpConnection?.PersonActorId,
                ProposalStatus = kind is NhAssistantAgentNotificationKind.ConsultationProposalDecided or NhAssistantAgentNotificationKind.ExecutionReported
                    ? approval?.Status
                    : null
            };
            var recipients = await RecipientsAsync(context, thread, cancellationToken);
            if (recipients.Length == 0)
            {
                return;
            }

            await DeliverAsync(context, recipients, cancellationToken);
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

    /// <summary>
    /// Tells people once about the proposals of one run that wait for them, or of one consultation
    /// when <paramref name="runId"/> is <see langword="null"/> (RN1, RN3, RN6). The policy is asked
    /// per notice with <paramref name="kind"/>, the per-object kind it has always seen; a recipient
    /// then hears once, in the bundled kind, about every object the policy picked them for, and never
    /// about another. One context goes to Web Push and the channels per group of recipients with the
    /// same objects and the same object to open: the run's own thread for its initiator
    /// (<paramref name="runInitiatorActorId"/>, when <paramref name="runConversationId"/> waits for
    /// them), otherwise the object with the most proposals. Shadow work notifies nobody, and the
    /// whole application's objects reach only people who see them.
    /// </summary>
    public async Task NotifyProposalsAsync(
        NhAssistantAgentNotificationKind kind,
        Guid? runId,
        Guid? runConversationId,
        string? runInitiatorActorId,
        bool shadow,
        IReadOnlyList<NhAssistantProposalNotice> notices,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(notices);
        var bundledKind = BundledKindOf(kind)
            ?? throw new ArgumentOutOfRangeException(nameof(kind), kind, "Only waiting, expiring and escalated proposals are bundled.");
        if (shadow)
        {
            return;
        }

        // The objects in the order the notifications name them, and per recipient what waits for
        // them at each object.
        var order = new List<Guid>();
        var waiting = new Dictionary<string, Dictionary<Guid, NhAssistantWaitingShare>>(StringComparer.Ordinal);
        foreach (var notice in notices)
        {
            if (IsShadow(notice.Thread, notice.Approvals))
            {
                continue;
            }

            NhAssistantAgentNotificationContext context;
            string[] recipients;
            try
            {
                context = (await ContextAsync(kind, notice.Thread, runId, notice.Approvals.FirstOrDefault(), notice.InitiatorActorId, cancellationToken)) with
                {
                    ProposalCount = notice.ProposalCount
                };
                recipients = await RecipientsAsync(context, notice.Thread, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
            {
                logger.LogWarning(
                    "Agent notification {Kind} for thread {ConversationId} was not sent ({ExceptionType}).",
                    bundledKind,
                    notice.Thread.ConversationId,
                    exception.GetType().Name);
                continue;
            }

            if (!order.Contains(context.ConversationId))
            {
                order.Add(context.ConversationId);
            }
            foreach (var recipient in recipients)
            {
                if (!waiting.TryGetValue(recipient, out var shares))
                {
                    shares = [];
                    waiting[recipient] = shares;
                }
                if (!shares.TryGetValue(context.ConversationId, out var share))
                {
                    share = new NhAssistantWaitingShare(context);
                    shares[context.ConversationId] = share;
                }
                share.Add(notice);
            }
        }

        // The run's own thread comes first in every notification that names it.
        if (runConversationId is { } own && order.Remove(own))
        {
            order.Insert(0, own);
        }

        var groups = new Dictionary<string, (NhAssistantAgentNotificationContext Context, List<string> Recipients)>(StringComparer.Ordinal);
        foreach (var (recipient, shares) in waiting)
        {
            var objects = order
                .Where(shares.ContainsKey)
                .Select(conversationId => shares[conversationId])
                .ToArray();
            var opened = OpenedBy(recipient, runInitiatorActorId, runConversationId, shares, objects);
            // The same objects with the same proposals, and the same object to open.
            var key = string.Join(';', objects.Select(share => share.Key)) + ">" + opened.Context.ConversationId.ToString("N");
            if (!groups.TryGetValue(key, out var group))
            {
                group = (Bundle(bundledKind, runId, runInitiatorActorId, opened, objects), []);
                groups[key] = group;
            }
            group.Recipients.Add(recipient);
        }

        foreach (var group in groups.Values)
        {
            try
            {
                await DeliverAsync(group.Context, group.Recipients, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
            {
                logger.LogWarning(
                    "Agent notification {Kind} for thread {ConversationId} was not sent ({ExceptionType}).",
                    bundledKind,
                    group.Context.ConversationId,
                    exception.GetType().Name);
            }
        }
    }

    /// <summary>
    /// The kind the channels receive for the proposals of a run that the policy is asked about with
    /// <paramref name="kind"/>, or <see langword="null"/> for a kind that is not bundled.
    /// </summary>
    internal static NhAssistantAgentNotificationKind? BundledKindOf(NhAssistantAgentNotificationKind kind)
    {
        return kind switch
        {
            NhAssistantAgentNotificationKind.RunEndedWithProposals => NhAssistantAgentNotificationKind.RunProposalsWaiting,
            NhAssistantAgentNotificationKind.ProposalExpiring => NhAssistantAgentNotificationKind.RunProposalsExpiring,
            NhAssistantAgentNotificationKind.ProposalEscalated => NhAssistantAgentNotificationKind.RunProposalsEscalated,
            _ => null
        };
    }

    /// <summary>
    /// The object a notification opens for <paramref name="recipient"/> (RN5): the run's own thread
    /// for its initiator when proposals wait for them there, otherwise the object with the most
    /// proposals, the first of them on a tie.
    /// </summary>
    private static NhAssistantWaitingShare OpenedBy(
        string recipient,
        string? runInitiatorActorId,
        Guid? runConversationId,
        IReadOnlyDictionary<Guid, NhAssistantWaitingShare> shares,
        IReadOnlyList<NhAssistantWaitingShare> objects)
    {
        if (string.Equals(recipient, runInitiatorActorId, StringComparison.Ordinal)
            && runConversationId is { } own
            && shares.TryGetValue(own, out var ownShare))
        {
            return ownShare;
        }

        return objects.MaxBy(share => share.ProposalCount)!;
    }

    /// <summary>
    /// The context of one group: the object it opens as its subject and conversation, every object
    /// of the group with its count, and the proposal and its tool when the group counts exactly one;
    /// the expiry is the earliest of its proposals.
    /// </summary>
    private static NhAssistantAgentNotificationContext Bundle(
        NhAssistantAgentNotificationKind bundledKind,
        Guid? runId,
        string? runInitiatorActorId,
        NhAssistantWaitingShare opened,
        IReadOnlyList<NhAssistantWaitingShare> objects)
    {
        var approvals = objects.SelectMany(share => share.Approvals).ToArray();
        var total = objects.Sum(share => share.ProposalCount);
        var single = total == 1 && approvals.Length == 1 ? approvals[0] : null;
        return opened.Context with
        {
            Kind = bundledKind,
            RunId = runId,
            ApprovalId = single?.Id,
            InitiatorActorId = runInitiatorActorId,
            ProposalCount = total,
            Objects = objects
                .Select(share => new NhAssistantAgentNotificationObject(
                    share.Context.Subject,
                    share.Context.SubjectDisplayName,
                    share.Context.ConversationId,
                    share.ProposalCount))
                .ToArray(),
            ToolId = single?.ToolId,
            ExpiresAt = approvals.Length == 0 ? null : approvals.Min(approval => approval.ExpiresAt)
        };
    }

    /// <summary>
    /// Shadow work notifies nobody: a shadow proposal, a shadow thread or the thread of a variant.
    /// </summary>
    private bool IsShadow(AssistantThread thread, IReadOnlyList<AssistantApproval> approvals)
    {
        return thread.Shadow
            || approvals.Any(approval => approval.Status == NhAssistantApprovalStatuses.Shadow)
            || (registration.AgentRuns.TryGetValue(thread.AgentId, out var options) && options.ShadowOfAgentId is not null);
    }

    /// <summary>
    /// The context of one event in <paramref name="thread"/>, with the subject's member snapshot.
    /// </summary>
    private async Task<NhAssistantAgentNotificationContext> ContextAsync(
        NhAssistantAgentNotificationKind kind,
        AssistantThread thread,
        Guid? runId,
        AssistantApproval? approval,
        string? initiatorActorId,
        CancellationToken cancellationToken)
    {
        var subject = new NhAiSubjectRef(thread.SubjectType, thread.SubjectId);
        IReadOnlyList<NhAiSubjectMember> members;
        if (NhAiSystemSubjects.IsSystem(subject))
        {
            members = await SystemMembersAsync(thread, subject, cancellationToken);
        }
        else
        {
            if (thread.MembersRefreshedAt is null)
            {
                // The first notification of a thread fills its member snapshot.
                await subjects.RefreshAsync(subject, cancellationToken);
            }

            members = (await runs.GetMembersAsync(thread.ScopeKey, subject, cancellationToken))
                .Select(member => new NhAiSubjectMember(member.ActorId, member.Role) { DisplayName = member.DisplayName })
                .ToArray();
        }

        return new NhAssistantAgentNotificationContext(
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
            ToolId = approval?.ToolId,
            ExpiresAt = approval?.ExpiresAt
        };
    }

    /// <summary>
    /// The people the policy picks for <paramref name="context"/>, without agents and unusable ids.
    /// Whatever the policy picks, the whole application's work reaches only people who see it.
    /// </summary>
    private async Task<string[]> RecipientsAsync(
        NhAssistantAgentNotificationContext context,
        AssistantThread thread,
        CancellationToken cancellationToken)
    {
        var recipients = (await policy.SelectRecipientsAsync(context, cancellationToken))
            .Where(actorId => !string.IsNullOrWhiteSpace(actorId) && actorId.Length <= 256)
            .Where(actorId => !actorId.StartsWith(NhAssistantAgentEvents.AgentActorPrefix, StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (NhAiSystemSubjects.IsSystem(context.Subject))
        {
            recipients = await ReadersAsync(context.Subject, thread.AgentId, recipients, cancellationToken);
        }

        return recipients;
    }

    /// <summary>
    /// Hands one notification to Web Push and every channel; a channel that fails does not stop the
    /// others.
    /// </summary>
    private async Task DeliverAsync(
        NhAssistantAgentNotificationContext context,
        IReadOnlyList<string> recipients,
        CancellationToken cancellationToken)
    {
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
                    context.Kind,
                    exception.GetType().Name);
            }
        }
    }

    /// <summary>
    /// The whole application lists no members, so the people of its thread stand in for them: those
    /// who take part in the thread's conversation and those who decided its proposals, at most
    /// <see cref="MaxSystemRecipients"/>, each with the role the resolver gives them now, and only
    /// those who pass the agent's view policy. The notification policy then picks the approvers among
    /// them as for any subject.
    /// </summary>
    private async Task<IReadOnlyList<NhAiSubjectMember>> SystemMembersAsync(
        AssistantThread thread,
        NhAiSubjectRef subject,
        CancellationToken cancellationToken)
    {
        var members = new List<NhAiSubjectMember>();
        foreach (var actorId in await runs.GetThreadPeopleAsync(thread.ConversationId, MaxSystemRecipients, cancellationToken))
        {
            var access = await subjects.ResolveForAgentAsync(subject, thread.AgentId, actorId, cancellationToken);
            if (access.Role != NhAiSubjectRole.None)
            {
                members.Add(new NhAiSubjectMember(actorId, access.Role));
            }
        }

        return members;
    }

    /// <summary>
    /// The recipients who may read the agent's thread of the whole application: they pass its view policy.
    /// </summary>
    private async Task<string[]> ReadersAsync(
        NhAiSubjectRef subject,
        string agentId,
        IReadOnlyList<string> recipients,
        CancellationToken cancellationToken)
    {
        var readers = new List<string>(recipients.Count);
        foreach (var actorId in recipients)
        {
            if (await subjects.MayReadAgentThreadAsync(subject, agentId, actorId, cancellationToken))
            {
                readers.Add(actorId);
            }
        }

        return [.. readers];
    }

    /// <summary>
    /// The most people of a thread of the whole application one notification considers.
    /// </summary>
    internal const int MaxSystemRecipients = 50;
}

/// <summary>
/// One question to the notification policy about the proposals of a run: those at one object when
/// the run ends or waits, or one proposal for a reminder or an escalation, with the initiator the
/// policy's context names. <paramref name="ProposalCount"/> counts the proposals, also when they were
/// not loaded.
/// </summary>
internal sealed record NhAssistantProposalNotice(
    AssistantThread Thread,
    IReadOnlyList<AssistantApproval> Approvals,
    string? InitiatorActorId,
    int ProposalCount);

/// <summary>
/// What waits for one recipient at one object: the object's context as the policy saw it first, and
/// the proposals of the notices the policy picked the recipient for.
/// </summary>
internal sealed class NhAssistantWaitingShare(NhAssistantAgentNotificationContext context)
{
    private readonly List<AssistantApproval> _approvals = [];

    public NhAssistantAgentNotificationContext Context { get; } = context;

    public int ProposalCount { get; private set; }

    public IReadOnlyList<AssistantApproval> Approvals => _approvals;

    /// <summary>
    /// The object, its count and its proposals: recipients with the same keys share a notification.
    /// </summary>
    public string Key => $"{Context.ConversationId:N}:{ProposalCount}:"
        + string.Join(',', _approvals.Select(approval => approval.Id.ToString("N")).Order(StringComparer.Ordinal));

    public void Add(NhAssistantProposalNotice notice)
    {
        ProposalCount += notice.ProposalCount;
        _approvals.AddRange(notice.Approvals);
    }
}

/// <summary>
/// Records content-free business audit events of agent runs: accepted and rejected triggers,
/// ended runs, executed and resolved proposals, proposal feedback and its export, and kill-switch
/// use.
/// </summary>
internal sealed class NhAssistantAgentEvents(
    IEnumerable<INhAssistantBusinessAuditSink> businessSinks,
    NhAssistantRegistrationState registration,
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

    /// <summary>
    /// Records that a run ended, with its content-free facts: what started it, whether it was a
    /// shadow run, its skill and skill version, the version of its data trigger, how many model calls it made and its status apart
    /// from its failure code. <c>ResultCode</c> keeps <c>status:code</c> as before. A run created
    /// before its trigger kind was recorded takes the kind of its trigger as registered now.
    /// </summary>
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
                RunStatus = status,
                RunFailureCode = failureCode,
                RunId = run.Id,
                TriggerId = run.TriggerId,
                TriggerKind = run.TriggerKind ?? NhAssistantTriggerKinds.Of(run.TriggerId, run.ReplayOfRunId, registration.Triggers),
                SubjectType = thread.SubjectType,
                SubjectId = thread.SubjectId,
                InitiatorActorId = run.InitiatorActorId,
                SkillId = run.SkillId,
                SkillVersion = run.SkillVersion,
                TriggerVersion = run.TriggerVersion,
                Shadow = run.Shadow,
                ModelCallCount = run.ModelCalls
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
        return ProposalAsync(kind, thread, approval, actorId, resultCode, null, null, cancellationToken);
    }

    /// <summary>
    /// Records a proposal event with how the application said its execution ended: the outcome
    /// (<see cref="NhAssistantApplicationExecutionCodes"/>) and the application's code, or the
    /// assistant's own execution code such as <c>report-overdue</c>; never a summary.
    /// </summary>
    public Task ProposalAsync(
        NhAssistantAuditEventKind kind,
        AssistantThread thread,
        AssistantApproval approval,
        string actorId,
        string resultCode,
        string? executionOutcome,
        string? executionCode,
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
                ConsultationId = approval.ConsultationId,
                ConsultationRoute = NhAssistantConsultationRoutes.Of(approval.ConsultationId, approval.Mode, approval.GrantSource),
                ExecutionOutcome = executionOutcome,
                ExecutionCode = executionCode
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
                ConsultationId = approval.ConsultationId,
                ConsultationRoute = NhAssistantConsultationRoutes.Of(approval.ConsultationId, approval.Mode, approval.GrantSource)
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
                ConsultationId = approval.ConsultationId,
                ConsultationRoute = NhAssistantConsultationRoutes.Of(approval.ConsultationId, approval.Mode, approval.GrantSource)
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
                ProposalCount = consultation.ProposalCount,
                ConsultationSource = consultation.ConsultationSource,
                ConsultationVersion = consultation.ConsultationVersion
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
    /// Records a hand-off from a run of <paramref name="sourceAgentId"/> to
    /// <paramref name="targetAgentId"/>: the outcome or the refusal code, never the message.
    /// </summary>
    public Task HandOffAsync(
        string sourceAgentId,
        Guid sourceRunId,
        string targetAgentId,
        NhAiSubjectRef subject,
        string outcome,
        Guid? conversationId,
        Guid? runId,
        CancellationToken cancellationToken)
    {
        return RecordAsync(
            new NhAssistantAuditEvent(
                NhAssistantAuditEventKind.AgentHandedOff,
                conversationId ?? Guid.Empty,
                Guid.Empty,
                AgentActorPrefix + sourceAgentId,
                targetAgentId,
                0,
                DateTimeOffset.UtcNow)
            {
                ResultCode = outcome,
                RunId = runId,
                TriggerId = NhAssistantTriggerIds.HandOff,
                SubjectType = subject.Type,
                SubjectId = subject.Id,
                AskingAgentId = sourceAgentId,
                SourceRunId = sourceRunId
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
            NhAssistantAgentRunStates.Off => (NhAssistantAuditEventKind.AdminAgentSwitchedOff, "off"),
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

    public Task AgentShadowChangedAsync(
        string agentId,
        string actorId,
        bool? shadow,
        CancellationToken cancellationToken,
        string? skillId = null)
    {
        return RecordAsync(
            new NhAssistantAuditEvent(NhAssistantAuditEventKind.AdminAgentShadowChanged, Guid.Empty, Guid.Empty, actorId, agentId, 0, DateTimeOffset.UtcNow)
            {
                ObjectId = agentId,
                ResultCode = shadow switch { true => "shadow", false => "live", null => "code" },
                SkillId = skillId
            },
            cancellationToken);
    }

    public Task AgentConsultationRoutesChangedAsync(
        string agentId,
        string actorId,
        NhAssistantConsultationRouteOverrides routes,
        CancellationToken cancellationToken)
    {
        static string Of(bool? value) => value switch
        {
            true => "on",
            false => "off",
            _ => "code"
        };

        return RecordAsync(
            new NhAssistantAuditEvent(NhAssistantAuditEventKind.AdminAgentConsultationRoutesChanged, Guid.Empty, Guid.Empty, actorId, agentId, 0, DateTimeOffset.UtcNow)
            {
                ResultCode = $"{NhAssistantConsultationRoutes.AskerDecides}={Of(routes.AskerDecides)};"
                    + $"{NhAssistantConsultationRoutes.SubjectAgentRights}={Of(routes.SubjectAgentRights)};"
                    + $"{NhAssistantConsultationRoutes.SubjectAskerRights}={Of(routes.SubjectAskerRights)}"
            },
            cancellationToken);
    }

    public Task AgentServiceAccountChangedAsync(
        string agentId,
        string actorId,
        string accountableActorId,
        bool overridden,
        CancellationToken cancellationToken)
    {
        return RecordAsync(
            new NhAssistantAuditEvent(NhAssistantAuditEventKind.AdminAgentServiceAccountChanged, Guid.Empty, Guid.Empty, actorId, agentId, 0, DateTimeOffset.UtcNow)
            {
                ObjectId = accountableActorId,
                ResultCode = overridden ? "set" : "code"
            },
            cancellationToken);
    }

    /// <param name="instructionsVersion">The asset of the application the version takes its
    /// instructions from, as <c>&lt;asset-id&gt;@&lt;version&gt;</c>; <see langword="null"/> for inline
    /// instructions or none.</param>
    public Task SkillDefinitionChangedAsync(
        string agentId,
        string skillId,
        int version,
        string? instructionsHash,
        string change,
        string actorId,
        CancellationToken cancellationToken,
        string? instructionsVersion = null)
    {
        return RecordAsync(
            new NhAssistantAuditEvent(NhAssistantAuditEventKind.AdminAgentSkillDefinitionChanged, Guid.Empty, Guid.Empty, actorId, agentId, version, DateTimeOffset.UtcNow)
            {
                ObjectId = skillId,
                SkillId = skillId,
                InstructionsVersion = instructionsVersion,
                InstructionsHash = instructionsHash,
                ResultCode = change
            },
            cancellationToken);
    }

    public Task RunSettingsChangedAsync(
        string agentId,
        int version,
        string change,
        string actorId,
        CancellationToken cancellationToken)
    {
        return RecordAsync(
            new NhAssistantAuditEvent(NhAssistantAuditEventKind.AdminAgentRunSettingsChanged, Guid.Empty, Guid.Empty, actorId, agentId, version, DateTimeOffset.UtcNow)
            {
                ObjectId = agentId,
                ResultCode = change
            },
            cancellationToken);
    }

    public Task TriggerDefinitionChangedAsync(
        string agentId,
        string triggerId,
        string? skillId,
        int version,
        string change,
        string actorId,
        CancellationToken cancellationToken)
    {
        return RecordAsync(
            new NhAssistantAuditEvent(NhAssistantAuditEventKind.AdminAgentTriggerDefinitionChanged, Guid.Empty, Guid.Empty, actorId, agentId, version, DateTimeOffset.UtcNow)
            {
                ObjectId = triggerId,
                TriggerId = triggerId,
                SkillId = skillId,
                ResultCode = change
            },
            cancellationToken);
    }

    public Task ConsultationDefinitionChangedAsync(
        string agentId,
        string subjectType,
        int version,
        string? instructionsHash,
        string change,
        string actorId,
        CancellationToken cancellationToken)
    {
        return RecordAsync(
            new NhAssistantAuditEvent(NhAssistantAuditEventKind.AdminAgentConsultationDefinitionChanged, Guid.Empty, Guid.Empty, actorId, agentId, version, DateTimeOffset.UtcNow)
            {
                ObjectId = subjectType,
                SubjectType = subjectType,
                InstructionsHash = instructionsHash,
                ConsultationSource = NhAssistantConsultationSources.Data,
                ConsultationVersion = version,
                ResultCode = change
            },
            cancellationToken);
    }

    public Task HandOffDefinitionChangedAsync(
        string agentId,
        string subjectType,
        int version,
        string change,
        string actorId,
        CancellationToken cancellationToken)
    {
        return RecordAsync(
            new NhAssistantAuditEvent(NhAssistantAuditEventKind.AdminAgentHandOffDefinitionChanged, Guid.Empty, Guid.Empty, actorId, agentId, version, DateTimeOffset.UtcNow)
            {
                ObjectId = subjectType,
                SubjectType = subjectType,
                ResultCode = change
            },
            cancellationToken);
    }

    public Task EvaluationExportedAsync(string agentId, string variantId, string actorId, CancellationToken cancellationToken)
    {
        return RecordAsync(
            new NhAssistantAuditEvent(
                NhAssistantAuditEventKind.AdminAgentEvaluationExported,
                Guid.Empty,
                Guid.Empty,
                actorId,
                agentId,
                0,
                DateTimeOffset.UtcNow)
            {
                ObjectId = variantId,
                ResultCode = "exported"
            },
            cancellationToken);
    }

    public Task ReplayStartedAsync(Guid replayId, string variantId, string sourceAgentId, string actorId, CancellationToken cancellationToken)
    {
        return RecordAsync(
            new NhAssistantAuditEvent(
                NhAssistantAuditEventKind.AdminAgentReplayStarted,
                Guid.Empty,
                Guid.Empty,
                actorId,
                variantId,
                0,
                DateTimeOffset.UtcNow)
            {
                ObjectId = replayId.ToString("N"),
                AskingAgentId = sourceAgentId,
                ResultCode = "started"
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
