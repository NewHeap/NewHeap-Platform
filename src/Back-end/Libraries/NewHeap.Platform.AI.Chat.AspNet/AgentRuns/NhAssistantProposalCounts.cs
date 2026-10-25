using NewHeap.Platform.AI.Chat.Persistence;

namespace NewHeap.Platform.AI.Chat.AspNet.AgentRuns;

/// <summary>
/// The statuses the approval inbox lists (<c>GET approvals/inbox?status=</c>) and
/// <see cref="INhAssistantProposalCounts"/> counts: what waits for a decision, what waits for the
/// application's report and what waits for an approver to resolve an uncertain execution.
/// </summary>
public static class NhAssistantInboxStatuses
{
    /// <summary>
    /// Proposals that wait for a decision, before their expiry; the inbox's default.
    /// </summary>
    public const string Pending = NhAssistantApprovalStatuses.Pending;

    /// <summary>
    /// Approved proposals of tools the application executes (<see cref="NhAssistantBuilder.ExecuteInApplication(string, TimeSpan)"/>)
    /// that wait for its report, before the report falls due.
    /// </summary>
    public const string AwaitingApplication = NhAssistantApprovalStatuses.AwaitingApplication;

    /// <summary>
    /// Proposals whose execution is uncertain until an approver resolves it.
    /// </summary>
    public const string ExecutionUnknown = NhAssistantApprovalStatuses.ExecutionUnknown;

    /// <summary>
    /// Every status the inbox lists, in this order.
    /// </summary>
    public static IReadOnlyList<string> All { get; } = [Pending, AwaitingApplication, ExecutionUnknown];

    /// <summary>
    /// Whether the inbox lists <paramref name="status"/>.
    /// </summary>
    public static bool IsValid(string? status)
    {
        return status is Pending or AwaitingApplication or ExecutionUnknown;
    }
}

/// <summary>
/// Counts the proposals a person's approval inbox lists, per status, for example for a daily digest
/// of what waits. It is a server API for the application's code; there is no endpoint for it. The
/// counts follow the inbox's visibility exactly: the proposals of subjects the person approves
/// according to the member snapshot, those of runs the person started in the run's own thread, and
/// those of the whole application (<c>nh-system</c>) where the person's live role is approver or the
/// person started the run, both only when the person passes the agent's view policy. It returns ids
/// and counts only, never proposal content.
/// </summary>
public interface INhAssistantProposalCounts
{
    /// <summary>
    /// How many proposals the inbox of <paramref name="person"/> lists in each of
    /// <paramref name="statuses"/>, keyed by status; a status without proposals counts zero.
    /// </summary>
    /// <param name="person">The person, as the application authenticated them. Their actor id and tenant
    /// decide what they see; the whole application's roles are evaluated for them as for the inbox.</param>
    /// <param name="statuses">Statuses of <see cref="NhAssistantInboxStatuses"/>; a repeated status is
    /// counted once.</param>
    /// <exception cref="ArgumentException">A status is not one the inbox lists, or the person has no
    /// actor id.</exception>
    Task<IReadOnlyDictionary<string, int>> CountAsync(
        NhAssistantDecidingPerson person,
        IReadOnlyCollection<string> statuses,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Counts with the inbox's own query, one status at a time, so a count never differs from the total
/// of the inbox with that status.
/// </summary>
internal sealed class NhAssistantProposalCounts(
    NhAssistantRunStore runs,
    NhAssistantSystemApprovers systemApprovers) : INhAssistantProposalCounts
{
    public async Task<IReadOnlyDictionary<string, int>> CountAsync(
        NhAssistantDecidingPerson person,
        IReadOnlyCollection<string> statuses,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(person);
        ArgumentException.ThrowIfNullOrWhiteSpace(person.ActorId, nameof(person));
        ArgumentNullException.ThrowIfNull(statuses);
        foreach (var status in statuses)
        {
            if (!NhAssistantInboxStatuses.IsValid(status))
            {
                throw new ArgumentException(
                    $"The inbox counts the statuses {string.Join(", ", NhAssistantInboxStatuses.All)}; '{status}' is none of them.",
                    nameof(statuses));
            }
        }

        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var status in statuses.Distinct(StringComparer.Ordinal))
        {
            // The whole application lists no members: its approvers are evaluated now, per status.
            var system = await systemApprovers.InboxAsync(person.ActorId, person.TenantId, cancellationToken, status);
            counts[status] = await runs.CountInboxAsync(
                person.ActorId,
                new NhAssistantInboxFilter(null, null, null, null) { Status = status },
                system,
                cancellationToken);
        }

        return counts;
    }
}
