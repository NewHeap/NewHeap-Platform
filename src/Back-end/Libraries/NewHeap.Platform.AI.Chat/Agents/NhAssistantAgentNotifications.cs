namespace NewHeap.Platform.AI.Chat;

/// <summary>
/// The moments an agent run asks people outside the thread for attention.
/// </summary>
public enum NhAssistantAgentNotificationKind
{
    /// <summary>
    /// A run ended and left proposals that wait for a decision.
    /// </summary>
    RunEndedWithProposals = 0,

    /// <summary>
    /// A proposal still waits; a reminder of its approval policy is due.
    /// </summary>
    ProposalExpiring = 1,

    /// <summary>
    /// A proposal was not decided in time and moved to its escalation policy.
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
    RequestAnswered = 5
}

/// <summary>
/// The facts of one agent notification. It carries identifiers and codes, never message, tool or
/// subject content beyond the subject's display name.
/// </summary>
/// <param name="Members">The member snapshot of the subject: everyone with a role on it.</param>
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
    /// The number of proposals that wait, for <see cref="NhAssistantAgentNotificationKind.RunEndedWithProposals"/>.
    /// </summary>
    public int ProposalCount { get; init; }

    public string? ToolId { get; init; }

    public DateTimeOffset? ExpiresAt { get; init; }

    /// <summary>
    /// The failure code of a failed run.
    /// </summary>
    public string? FailureCode { get; init; }
}

/// <summary>
/// Decides who is told about an agent event. Recipients come from the subject's member snapshot,
/// not from whoever owns the run's background operation. Register a policy with
/// <see cref="NhAssistantBuilder.UseAgentNotificationPolicy{TPolicy}"/>; wrap
/// <see cref="NhAssistantDefaultAgentNotificationPolicy"/> to refine the default.
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
/// A channel must not throw for a single failed delivery; failures never affect the run.
/// </summary>
public interface INhAssistantAgentNotificationChannel
{
    Task DeliverAsync(
        NhAssistantAgentNotificationContext context,
        IReadOnlyList<string> recipientActorIds,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// The default recipients: the subject's approvers for proposals and uncertain executions, the
/// approvers plus the initiator for a failed run, and only the initiator for an answered request.
/// Shadow runs never notify anyone.
/// </summary>
public sealed class NhAssistantDefaultAgentNotificationPolicy : INhAssistantAgentNotificationPolicy
{
    public ValueTask<IReadOnlyList<string>> SelectRecipientsAsync(
        NhAssistantAgentNotificationContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        if (context.Kind == NhAssistantAgentNotificationKind.RequestAnswered)
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
