namespace NewHeap.Platform.AI.Chat;

/// <summary>
/// The thresholds by which the administration's view per kind of action
/// (<c>GET admin/proposal-kinds</c>) marks an agent's kind of action as a candidate for running
/// without approval (<c>NewHeap:AI:Assistant:RoutineCandidates</c>, or
/// <c>NhAssistantBuilder.ConfigureRoutineCandidates</c>). A candidate is a suggestion from the numbers
/// of the report's period only: it changes nothing about how proposals are decided. A kind of action is
/// a candidate for an agent when it meets every threshold.
/// </summary>
public sealed class NhAssistantRoutineCandidateOptions
{
    /// <summary>The default of <see cref="MinimumDecisions"/>.</summary>
    public const int DefaultMinimumDecisions = 50;

    /// <summary>The default of <see cref="MinimumApprovalRate"/>.</summary>
    public const double DefaultMinimumApprovalRate = 0.98;

    /// <summary>The default of <see cref="QuietPeriod"/>.</summary>
    public static readonly TimeSpan DefaultQuietPeriod = TimeSpan.FromDays(7);

    /// <summary>The largest <see cref="MinimumDecisions"/>.</summary>
    internal const int MaxMinimumDecisions = 100_000;

    /// <summary>The longest <see cref="QuietPeriod"/>, the longest period of the report.</summary>
    internal static readonly TimeSpan MaxQuietPeriod = TimeSpan.FromDays(366);

    /// <summary>
    /// The fewest decisions (approved plus rejected) of the period: 50 by default, from 1 to 100,000.
    /// Fewer decisions say too little about a kind of action.
    /// </summary>
    public int MinimumDecisions { get; set; } = DefaultMinimumDecisions;

    /// <summary>
    /// The smallest share of the decisions that people approved, from 0 to 1: 0.98 by default, so at
    /// most one rejection in 50 decisions.
    /// </summary>
    public double MinimumApprovalRate { get; set; } = DefaultMinimumApprovalRate;

    /// <summary>
    /// How long before the end of the period nobody may have rejected a proposal of the kind: 7 days
    /// by default, from zero to 366 days. A recent rejection says that people still correct the agent.
    /// </summary>
    public TimeSpan QuietPeriod { get; set; } = DefaultQuietPeriod;

    /// <summary>
    /// How many approved proposals of the period may have closed as stale right before they ran or
    /// failed when they ran: none by default. Such a proposal was right when a person approved it but
    /// wrong when it executed, which running without approval would not have caught.
    /// </summary>
    public int MaximumStaleOrFailedAfterApproval { get; set; }

    /// <summary>
    /// A copy within the documented ranges: a value outside its range counts as its nearest bound.
    /// </summary>
    internal NhAssistantRoutineCandidateOptions Bounded()
    {
        var approvalRate = double.IsNaN(MinimumApprovalRate) ? DefaultMinimumApprovalRate : MinimumApprovalRate;
        return new NhAssistantRoutineCandidateOptions
        {
            MinimumDecisions = Math.Clamp(MinimumDecisions, 1, MaxMinimumDecisions),
            MinimumApprovalRate = Math.Clamp(approvalRate, 0, 1),
            QuietPeriod = QuietPeriod < TimeSpan.Zero
                ? TimeSpan.Zero
                : QuietPeriod > MaxQuietPeriod ? MaxQuietPeriod : QuietPeriod,
            MaximumStaleOrFailedAfterApproval = Math.Max(0, MaximumStaleOrFailedAfterApproval)
        };
    }
}

/// <summary>
/// Why the view per kind of action does not mark an agent's kind of action as a candidate for running
/// without approval: the thresholds of <see cref="NhAssistantRoutineCandidateOptions"/> it misses.
/// </summary>
public static class NhAssistantRoutineCandidateReasons
{
    /// <summary>Fewer decisions than <see cref="NhAssistantRoutineCandidateOptions.MinimumDecisions"/>.</summary>
    public const string TooFewDecisions = "too-few-decisions";

    /// <summary>A smaller share approved than <see cref="NhAssistantRoutineCandidateOptions.MinimumApprovalRate"/>.</summary>
    public const string ApprovalRateTooLow = "approval-rate-too-low";

    /// <summary>A rejection within <see cref="NhAssistantRoutineCandidateOptions.QuietPeriod"/> before the end of the period.</summary>
    public const string RecentRejection = "recent-rejection";

    /// <summary>
    /// More approved proposals that closed as stale before they ran or failed when they ran than
    /// <see cref="NhAssistantRoutineCandidateOptions.MaximumStaleOrFailedAfterApproval"/>.
    /// </summary>
    public const string StaleOrFailedAfterApproval = "stale-or-failed-after-approval";
}
