namespace NewHeap.Platform.AI.Chat;

/// <summary>
/// The moments an agent run asks people outside the thread for attention.
/// </summary>
/// <remarks>
/// The proposals of a run reach people bundled: the notification policy is asked per object with
/// <see cref="RunEndedWithProposals"/> and per proposal with <see cref="ProposalExpiring"/> and
/// <see cref="ProposalEscalated"/>, and Web Push and the channels then receive one
/// <see cref="RunProposalsWaiting"/>, <see cref="RunProposalsExpiring"/> or
/// <see cref="RunProposalsEscalated"/> per group of recipients with the same objects, which
/// <see cref="NhAssistantAgentNotificationContext.Objects"/> names.
/// </remarks>
public enum NhAssistantAgentNotificationKind
{
    /// <summary>
    /// A run ended and left proposals that wait for a decision at one object. Only the notification
    /// policy sees this kind, once per object; the channels receive <see cref="RunProposalsWaiting"/>.
    /// </summary>
    RunEndedWithProposals = 0,

    /// <summary>
    /// A proposal still waits; a reminder of its approval policy is due. Only the notification policy
    /// sees this kind, once per proposal; the channels receive <see cref="RunProposalsExpiring"/>.
    /// </summary>
    ProposalExpiring = 1,

    /// <summary>
    /// A proposal was not decided in time and moved to its escalation policy. Only the notification
    /// policy sees this kind, once per proposal; the channels receive <see cref="RunProposalsEscalated"/>.
    /// </summary>
    ProposalEscalated = 2,

    /// <summary>
    /// An approved proposal stopped without a known outcome and waits for a person to resolve it.
    /// </summary>
    ExecutionUnknown = 3,

    /// <summary>
    /// A run failed permanently.
    /// </summary>
    RunFailed = 4,

    /// <summary>
    /// A run that another assistant requested for a person ended; the answer is in the agent's thread.
    /// </summary>
    RequestAnswered = 5,

    /// <summary>
    /// A background run left a personal MCP server out because the person's consent to background use
    /// or connection is missing; the person can allow or connect it in the assistant's preferences.
    /// Sent at most once per person and server a day.
    /// </summary>
    McpConnectionMissing = 6,

    /// <summary>
    /// A proposal of a consultation that waited for the subject's approvers ended: it was rejected,
    /// expired, closed or its execution ended, whether it executes with the asker's or the consulted
    /// agent's rights. Only the person who asked is told;
    /// <see cref="NhAssistantAgentNotificationContext.ProposalStatus"/> says how it ended. A proposal
    /// the asker decides in their own chat is not notified.
    /// </summary>
    ConsultationProposalDecided = 7,

    /// <summary>
    /// Proposals of one run wait for the recipients' decision, in the run's own thread and at the
    /// objects it placed them at: one notification per person and run, sent when the run ends or waits
    /// inline, and when a consultation leaves proposals at its subject (without a run).
    /// <see cref="NhAssistantAgentNotificationContext.Objects"/> names the objects that wait for these
    /// recipients and <see cref="NhAssistantAgentNotificationContext.ProposalCount"/> counts their
    /// proposals.
    /// </summary>
    RunProposalsWaiting = 8,

    /// <summary>
    /// Proposals of one run still wait; their reminders fell due in the same maintenance pass. One
    /// notification per person and run, with <see cref="NhAssistantAgentNotificationContext.Objects"/>.
    /// </summary>
    RunProposalsExpiring = 9,

    /// <summary>
    /// Proposals of one run were not decided in time and moved to their escalation policy in the same
    /// maintenance pass. One notification per person and run, with
    /// <see cref="NhAssistantAgentNotificationContext.Objects"/>.
    /// </summary>
    RunProposalsEscalated = 10,

    /// <summary>
    /// The application reported that its execution of an approved proposal ended
    /// (<see cref="INhAssistantProposalExecutionReports.ReportExecutionAsync"/>):
    /// <see cref="NhAssistantAgentNotificationContext.ProposalStatus"/> is <c>executed</c> or
    /// <c>execution-failed</c> and <see cref="NhAssistantAgentNotificationContext.FailureCode"/> the
    /// application's code. A report that needs attention, and a report deadline that passed, notify as
    /// <see cref="ExecutionUnknown"/>.
    /// </summary>
    ExecutionReported = 11
}

/// <summary>
/// One object in a notification about the proposals of a run: the subject, the name people see,
/// the agent's thread there and how many of its proposals there wait for the recipients.
/// </summary>
public sealed record NhAssistantAgentNotificationObject(
    NhAiSubjectRef Subject,
    string? SubjectDisplayName,
    Guid ConversationId,
    int ProposalCount);

/// <summary>
/// The facts of one agent notification. It carries identifiers and codes, never message, tool or
/// subject content beyond the subject's display name. A notification about the proposals of a run
/// (<see cref="NhAssistantAgentNotificationKind.RunProposalsWaiting"/>,
/// <see cref="NhAssistantAgentNotificationKind.RunProposalsExpiring"/> and
/// <see cref="NhAssistantAgentNotificationKind.RunProposalsEscalated"/>) names in
/// <paramref name="ConversationId"/>, <paramref name="Subject"/> and <paramref name="SubjectDisplayName"/>
/// the object it opens: the run's own thread for its initiator, otherwise the object with the most
/// proposals.
/// </summary>
/// <param name="Members">The member snapshot of the subject: everyone with a role on it.</param>
/// <param name="ApprovalId">The proposal; for the proposals of a run only when they count exactly one.</param>
/// <param name="InitiatorActorId">The person who caused the event; for the proposals of a run the
/// run's initiator, or the person who consulted the agent.</param>
public sealed record NhAssistantAgentNotificationContext(
    NhAssistantAgentNotificationKind Kind,
    string AgentId,
    Guid ConversationId,
    NhAiSubjectRef Subject,
    string? SubjectDisplayName,
    Guid? RunId,
    Guid? ApprovalId,
    string? InitiatorActorId,
    IReadOnlyList<NhAiSubjectMember> Members)
{
    /// <summary>
    /// The number of proposals that wait: at the object for
    /// <see cref="NhAssistantAgentNotificationKind.RunEndedWithProposals"/>, the proposal itself (1)
    /// for <see cref="NhAssistantAgentNotificationKind.ProposalExpiring"/> and
    /// <see cref="NhAssistantAgentNotificationKind.ProposalEscalated"/>, and all of
    /// <see cref="Objects"/> together for the proposals of a run.
    /// </summary>
    public int ProposalCount { get; init; }

    /// <summary>
    /// The objects of a notification about the proposals of a run, each with its proposals that wait
    /// for these recipients: only objects that wait for every one of them, the run's own thread first.
    /// Empty for every other kind.
    /// </summary>
    public IReadOnlyList<NhAssistantAgentNotificationObject> Objects { get; init; } = [];

    /// <summary>
    /// The tool of the proposal; for the proposals of a run only when they count exactly one.
    /// </summary>
    public string? ToolId { get; init; }

    /// <summary>
    /// When the proposal expires; for the proposals of a run the earliest expiry among them.
    /// </summary>
    public DateTimeOffset? ExpiresAt { get; init; }

    /// <summary>
    /// The failure code of a failed run, for <see cref="NhAssistantAgentNotificationKind.McpConnectionMissing"/>
    /// the code why the server was left out, and for <see cref="NhAssistantAgentNotificationKind.ExecutionReported"/>
    /// and an <see cref="NhAssistantAgentNotificationKind.ExecutionUnknown"/> of a tool the application
    /// executes the execution's code, such as the application's code of its report.
    /// </summary>
    public string? FailureCode { get; init; }

    /// <summary>The MCP server a run left out, for <see cref="NhAssistantAgentNotificationKind.McpConnectionMissing"/>.</summary>
    public string? McpServerId { get; init; }

    /// <summary>The display name of <see cref="McpServerId"/>.</summary>
    public string? McpServerDisplayName { get; init; }

    /// <summary>
    /// The person whose consent to background use or connection the server needs, for
    /// <see cref="NhAssistantAgentNotificationKind.McpConnectionMissing"/>.
    /// </summary>
    public string? McpConnectionPersonId { get; init; }

    /// <summary>
    /// How the proposal ended, one of <see cref="NhAssistantApprovalStatuses"/>, for
    /// <see cref="NhAssistantAgentNotificationKind.ConsultationProposalDecided"/> and
    /// <see cref="NhAssistantAgentNotificationKind.ExecutionReported"/>.
    /// </summary>
    public string? ProposalStatus { get; init; }
}

/// <summary>
/// Decides who is told about an agent event. Recipients come from the subject's member snapshot,
/// not from whoever owns the run's background operation. Register a policy with
/// <see cref="NhAssistantBuilder.UseAgentNotificationPolicy{TPolicy}"/>; wrap
/// <see cref="NhAssistantDefaultAgentNotificationPolicy"/> to refine the default. For the proposals
/// of a run the policy is asked once per object (<see cref="NhAssistantAgentNotificationKind.RunEndedWithProposals"/>)
/// or once per proposal (<see cref="NhAssistantAgentNotificationKind.ProposalExpiring"/>,
/// <see cref="NhAssistantAgentNotificationKind.ProposalEscalated"/>); the platform then tells each
/// recipient once per run about the objects the policy picked them for.
/// </summary>
public interface INhAssistantAgentNotificationPolicy
{
    ValueTask<IReadOnlyList<string>> SelectRecipientsAsync(
        NhAssistantAgentNotificationContext context,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Delivers agent notifications through a channel such as e-mail or an in-app inbox. Web Push
/// is built in. Register channels with <see cref="NhAssistantBuilder.AddAgentNotificationChannel{TChannel}"/>.
/// A channel must not throw for a single failed delivery; failures never affect the run. Proposals
/// arrive bundled per run, as <see cref="NhAssistantAgentNotificationKind.RunProposalsWaiting"/>,
/// <see cref="NhAssistantAgentNotificationKind.RunProposalsExpiring"/> and
/// <see cref="NhAssistantAgentNotificationKind.RunProposalsEscalated"/> with
/// <see cref="NhAssistantAgentNotificationContext.Objects"/>; a channel never receives the per-object
/// kinds the notification policy is asked with.
/// </summary>
public interface INhAssistantAgentNotificationChannel
{
    Task DeliverAsync(
        NhAssistantAgentNotificationContext context,
        IReadOnlyList<string> recipientActorIds,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// The default recipients: the subject's approvers for proposals, uncertain executions and the
/// application's execution reports, the
/// approvers plus the initiator for a failed run, only the initiator for an answered request and for
/// the end of a consultation proposal that waited at the subject, and only
/// the person whose consent or connection is missing for a left-out MCP server. Shadow runs never
/// notify anyone.
/// </summary>
public sealed class NhAssistantDefaultAgentNotificationPolicy : INhAssistantAgentNotificationPolicy
{
    public ValueTask<IReadOnlyList<string>> SelectRecipientsAsync(
        NhAssistantAgentNotificationContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        if (context.Kind == NhAssistantAgentNotificationKind.McpConnectionMissing)
        {
            // Only the person who can allow or connect the server; never the subject's approvers.
            return ValueTask.FromResult<IReadOnlyList<string>>(
                string.IsNullOrWhiteSpace(context.McpConnectionPersonId) ? [] : [context.McpConnectionPersonId]);
        }

        if (context.Kind is NhAssistantAgentNotificationKind.RequestAnswered or NhAssistantAgentNotificationKind.ConsultationProposalDecided)
        {
            return ValueTask.FromResult<IReadOnlyList<string>>(
                string.IsNullOrWhiteSpace(context.InitiatorActorId) ? [] : [context.InitiatorActorId]);
        }

        var recipients = context.Members
            .Where(member => member.Role >= NhAiSubjectRole.Approver)
            .Select(member => member.ActorId)
            .ToList();
        if (context.Kind == NhAssistantAgentNotificationKind.RunFailed
            && !string.IsNullOrWhiteSpace(context.InitiatorActorId))
        {
            recipients.Add(context.InitiatorActorId);
        }

        return ValueTask.FromResult<IReadOnlyList<string>>(recipients.Distinct(StringComparer.Ordinal).ToArray());
    }
}
