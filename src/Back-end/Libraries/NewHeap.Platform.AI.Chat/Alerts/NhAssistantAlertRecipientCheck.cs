using NewHeap.Platform.AI.Chat.Collaboration;
using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Chat.Runs;

namespace NewHeap.Platform.AI.Chat.Alerts;

/// <summary>
/// Whether one person may receive or read one alert now, and why not.
/// </summary>
/// <param name="Qualifies">The person qualifies.</param>
/// <param name="DropReason">Why not, one of <see cref="NhAssistantAlertDropReasons"/>; <see langword="null"/> when they qualify.</param>
internal sealed record NhAssistantAlertRecipientVerdict(bool Qualifies, string? DropReason)
{
    public static NhAssistantAlertRecipientVerdict Qualified { get; } = new(true, null);

    public static NhAssistantAlertRecipientVerdict Dropped(string reason)
    {
        return new NhAssistantAlertRecipientVerdict(false, reason);
    }
}

/// <summary>
/// The one check of a person against an alert, asked when the alert is raised, when a delivery goes out
/// and when the person reads it, so a person who lost access never receives or reads its text: the
/// recipient is a person (no agent, a bounded actor id), the application's principal factory still
/// rebuilds them for the subject in its division or tenant (<see cref="INhAssistantRecipientRules.IsResolvedForAsync"/>),
/// and the subject's resolver gives them at least <see cref="NhAiSubjectRole.Reader"/> on the subject now.
/// A read in a request also names the caller's tenant, which must reach the subject's
/// (<see cref="NhAssistantTenancy"/>). The verdict carries no content, so a caller can audit a drop.
/// </summary>
internal sealed class NhAssistantAlertRecipientCheck(
    NhAssistantSubjectAccessService subjects,
    INhAssistantRecipientRules recipients,
    NhAssistantTenancy tenancy)
{
    /// <summary>
    /// Checks <paramref name="actorId"/> against <paramref name="subject"/> as its resolver describes it
    /// now, without a caller's tenant: the check when an alert is raised and when a delivery goes out.
    /// </summary>
    public async ValueTask<NhAssistantAlertRecipientVerdict> CheckAsync(
        string actorId,
        NhAiSubjectInfo subject,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(subject);
        if (!IsPerson(actorId))
        {
            return NhAssistantAlertRecipientVerdict.Dropped(NhAssistantAlertDropReasons.NotAPerson);
        }
        if (!await recipients.IsResolvedForAsync(actorId, subject, cancellationToken))
        {
            return NhAssistantAlertRecipientVerdict.Dropped(NhAssistantAlertDropReasons.NotResolved);
        }

        var access = await subjects.ResolveAsync(subject.Subject, actorId, cancellationToken);
        return access.Has(NhAiSubjectRole.Reader)
            ? NhAssistantAlertRecipientVerdict.Qualified
            : NhAssistantAlertRecipientVerdict.Dropped(NhAssistantAlertDropReasons.NoAccess);
    }

    /// <summary>
    /// Checks <paramref name="actorId"/> against the stored <paramref name="alert"/>: its subject is
    /// described again, so a subject that no longer exists drops everyone. With
    /// <paramref name="callerTenantId"/>, the tenant of the caller of a read, the subject must be in the
    /// caller's tenant as well; <see langword="null"/> for a delivery, which has no caller.
    /// </summary>
    public async ValueTask<NhAssistantAlertRecipientVerdict> CheckAsync(
        string actorId,
        AssistantAlert alert,
        CancellationToken cancellationToken,
        bool inRequest = false,
        string? callerTenantId = null)
    {
        ArgumentNullException.ThrowIfNull(alert);
        if (!IsPerson(actorId))
        {
            return NhAssistantAlertRecipientVerdict.Dropped(NhAssistantAlertDropReasons.NotAPerson);
        }

        var subject = await subjects.DescribeAsync(new NhAiSubjectRef(alert.SubjectType, alert.SubjectId), cancellationToken);
        if (subject is null)
        {
            return NhAssistantAlertRecipientVerdict.Dropped(NhAssistantAlertDropReasons.SubjectGone);
        }
        if (inRequest && !tenancy.Includes(subject, callerTenantId))
        {
            return NhAssistantAlertRecipientVerdict.Dropped(NhAssistantAlertDropReasons.NoAccess);
        }

        return await CheckAsync(actorId, subject, cancellationToken);
    }

    /// <summary>
    /// Whether <paramref name="actorId"/> can be a person at all: a bounded id that names no agent.
    /// </summary>
    public static bool IsPerson(string? actorId)
    {
        return !string.IsNullOrWhiteSpace(actorId)
            && actorId.Length <= 256
            && actorId.Trim() == actorId
            && !actorId.StartsWith(NhAssistantAgentEvents.AgentActorPrefix, StringComparison.Ordinal)
            && !actorId.StartsWith(NhAssistantSpeakers.RuleActorPrefix, StringComparison.Ordinal);
    }
}
