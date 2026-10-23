using System.Text.RegularExpressions;
using NewHeap.Platform.Common.Models;

namespace NewHeap.Platform.AI.Chat;

/// <summary>
/// Executes the approved deferred proposals of the tools that the application declared with
/// <see cref="NhAssistantBuilder.ExecuteInApplication"/>, outside the tool's function, for example by
/// handing the work to an order system that confirms it hours later. Register it with
/// <see cref="NhAssistantBuilder.UseProposalExecutor{TExecutor}"/>; a host that declares such a tool
/// without an executor does not start.
/// <para>
/// Once a person approves such a proposal, by any path (the decision endpoints,
/// <c>INhAssistantProposalDecisionService</c> or a consultation's proposal that waited at its subject),
/// the assistant checks it as it checks every execution (the subject version when the policy requires an
/// unchanged subject and the proposal guards), marks it <see cref="NhAssistantApprovalStatuses.AwaitingApplication"/>
/// and calls <see cref="ExecuteAsync"/> from a background operation. The proposal then waits until the
/// application reports through <see cref="INhAssistantProposalExecutionReports.ReportExecutionAsync"/>, or
/// until the tool's report deadline makes it <see cref="NhAssistantApprovalStatuses.ExecutionUnknown"/>.
/// </para>
/// </summary>
public interface INhAssistantProposalExecutor
{
    /// <summary>
    /// Takes over the approved proposal of <paramref name="handOff"/>. Accept it and return quickly:
    /// start the work, keyed by <see cref="NhAssistantProposalHandOff.ApprovalId"/>, and report its
    /// outcome later, or at once when the work is already done. A hand-off can repeat, for example when an
    /// approver hands an uncertain proposal to the application again, so starting the same work twice must
    /// be safe.
    /// <para>
    /// A failed result says that the application does not execute the proposal at all: it closes as
    /// <see cref="NhAssistantApprovalStatuses.ExecutionFailed"/> with the result's code, as a report of
    /// <see cref="NhAssistantExecutionOutcome.Failed"/> would. An exception leaves it unknown whether the
    /// application took the work: the proposal becomes <see cref="NhAssistantApprovalStatuses.ExecutionUnknown"/>
    /// (<see cref="NhAssistantApplicationExecutionCodes.HandOffFailed"/>) and an approver resolves it.
    /// </para>
    /// </summary>
    Task<TaskResult> ExecuteAsync(NhAssistantProposalHandOff handOff, CancellationToken cancellationToken = default);
}

/// <summary>
/// An approved proposal that the assistant hands to the application's <see cref="INhAssistantProposalExecutor"/>.
/// </summary>
/// <param name="ApprovalId">The proposal, which a report names.</param>
/// <param name="Proposal">The approved payload as people approved it: the tool, its version and contract
/// hash, the canonical <see cref="NhAiProposal.Arguments"/>, the accountable owner and the proposal hash.
/// Execute exactly these arguments.</param>
/// <param name="DecidedByActorId">The person who approved it.</param>
/// <param name="DecidedAt">When they approved it, on the application's clock.</param>
/// <param name="AgentId">The agent that proposed it.</param>
/// <param name="Subject">The object the proposal waits at.</param>
/// <param name="TenantId">The tenant of the agent's thread; <see langword="null"/> without tenants and for
/// the whole application. A report passes it back (<see cref="NhAssistantExecutionReport.TenantId"/>).</param>
/// <param name="Attempt">The proposal's execution attempt of this hand-off, from 1: a later hand-off of
/// the same proposal, after an approver handed it to the application again, has a higher one.</param>
/// <param name="ReportDueAt">Until when the assistant waits for the report, on the application's clock;
/// afterwards the proposal becomes <see cref="NhAssistantApprovalStatuses.ExecutionUnknown"/>.</param>
public sealed record NhAssistantProposalHandOff(
    Guid ApprovalId,
    NhAiProposal Proposal,
    string DecidedByActorId,
    DateTimeOffset DecidedAt,
    string AgentId,
    NhAiSubjectRef Subject,
    string? TenantId,
    int Attempt,
    DateTimeOffset ReportDueAt)
{
    /// <summary>
    /// The proposal's id, as <see cref="NhAiProposal.ProposalId"/>.
    /// </summary>
    public Guid ProposalId => Proposal.ProposalId;

    /// <summary>
    /// The hash of the approved payload, which a report passes back.
    /// </summary>
    public string ProposalHash => Proposal.ProposalHash;

    /// <summary>
    /// The tool the application executes.
    /// </summary>
    public string ToolId => Proposal.ToolId;
}

/// <summary>
/// Takes the application's reports of the proposals it executes (<see cref="INhAssistantProposalExecutor"/>).
/// It is a server API for the application's code; there is no endpoint for it.
/// </summary>
public interface INhAssistantProposalExecutionReports
{
    /// <summary>
    /// Reports how the application's execution of a proposal that waits for it
    /// (<see cref="NhAssistantApprovalStatuses.AwaitingApplication"/>) ended. The report closes it:
    /// <see cref="NhAssistantExecutionOutcome.Completed"/> as <see cref="NhAssistantApprovalStatuses.Executed"/>,
    /// <see cref="NhAssistantExecutionOutcome.Failed"/> as <see cref="NhAssistantApprovalStatuses.ExecutionFailed"/>
    /// and <see cref="NhAssistantExecutionOutcome.NeedsAttention"/> as
    /// <see cref="NhAssistantApprovalStatuses.ExecutionUnknown"/>, which an approver resolves. The code
    /// becomes the proposal's execution result; the content-free audit records the outcome and the code,
    /// never the summary. The subject's approvers are notified, the summary is posted into the agent's
    /// thread as an application message that the model reads as information, never as instructions, and
    /// the follow-up run starts when the approval policy asks for one.
    /// <para>
    /// The application reports as application code, within one tenant: a report reaches a proposal of
    /// <see cref="NhAssistantExecutionReport.TenantId"/>, or of the whole application when that tenant
    /// takes part in it (no tenant, or the single-tenant mode's tenant). Any other proposal is
    /// <c>assistant-approval-not-found</c>, as an unknown one.
    /// </para>
    /// <para>
    /// The same report again, such as a retried delivery, changes nothing and returns the proposal with
    /// <see cref="NhAssistantExecutionReportReceipt.Duplicate"/>; it is not audited or notified again. A
    /// report about a proposal that does not wait for one fails with
    /// <c>assistant-execution-report-not-awaited</c>, a report whose hash differs with
    /// <c>assistant-proposal-hash-mismatch</c> and a summary longer than
    /// <see cref="NhAssistantExecutionReport.MaxSummaryLength"/> characters with
    /// <c>assistant-execution-report-invalid</c>; none of them changes anything.
    /// </para>
    /// </summary>
    /// <exception cref="ArgumentException">The outcome is undefined, or the code is not a dash-case code of
    /// at most <see cref="NhAssistantExecutionReport.MaxCodeLength"/> characters.</exception>
    Task<TaskResult<NhAssistantExecutionReportReceipt>> ReportExecutionAsync(
        NhAssistantExecutionReport report,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// How the application's execution of a proposal ended.
/// </summary>
public enum NhAssistantExecutionOutcome
{
    /// <summary>The work was done: the proposal becomes <see cref="NhAssistantApprovalStatuses.Executed"/>.</summary>
    Completed = 0,

    /// <summary>
    /// The application cannot say whether the work was done or a person has to look at it: the proposal
    /// becomes <see cref="NhAssistantApprovalStatuses.ExecutionUnknown"/> and an approver resolves it.
    /// </summary>
    NeedsAttention = 1,

    /// <summary>The work was not done: the proposal becomes <see cref="NhAssistantApprovalStatuses.ExecutionFailed"/>.</summary>
    Failed = 2
}

/// <summary>
/// The report of an execution for <see cref="INhAssistantProposalExecutionReports.ReportExecutionAsync"/>.
/// Build it from the hand-off with <see cref="For"/>, so it names the proposal, its hash and its tenant.
/// </summary>
/// <param name="ApprovalId">The proposal, as <see cref="NhAssistantProposalHandOff.ApprovalId"/>.</param>
/// <param name="ProposalHash">The hash of the payload the application executed, as
/// <see cref="NhAssistantProposalHandOff.ProposalHash"/>.</param>
/// <param name="Outcome">How the execution ended.</param>
/// <param name="Code">The application's code for the outcome, such as <c>order-placed</c> or
/// <c>supplier-unavailable</c>: a dash-case code of at most <see cref="MaxCodeLength"/> characters, which
/// the proposal shows as its execution result and the audit records.</param>
public sealed partial record NhAssistantExecutionReport(
    Guid ApprovalId,
    string ProposalHash,
    NhAssistantExecutionOutcome Outcome,
    string Code)
{
    /// <summary>
    /// The most characters of <see cref="Code"/>.
    /// </summary>
    public const int MaxCodeLength = 64;

    /// <summary>
    /// The most characters of <see cref="Summary"/>.
    /// </summary>
    public const int MaxSummaryLength = 2_000;

    /// <summary>
    /// What happened, in a few sentences, for the people of the agent's thread and the follow-up run,
    /// such as an order number. It is posted into the thread as an application message and the model
    /// reads it as information from the application, never as instructions; the audit never records it.
    /// At most <see cref="MaxSummaryLength"/> characters.
    /// </summary>
    public string? Summary { get; init; }

    /// <summary>
    /// The tenant the application reports in, as <see cref="NhAssistantProposalHandOff.TenantId"/>;
    /// <see langword="null"/> without tenants.
    /// </summary>
    public string? TenantId { get; init; }

    /// <summary>
    /// The report of the execution that <paramref name="handOff"/> asked for.
    /// </summary>
    public static NhAssistantExecutionReport For(
        NhAssistantProposalHandOff handOff,
        NhAssistantExecutionOutcome outcome,
        string code,
        string? summary = null)
    {
        ArgumentNullException.ThrowIfNull(handOff);
        return new NhAssistantExecutionReport(handOff.ApprovalId, handOff.ProposalHash, outcome, code)
        {
            Summary = summary,
            TenantId = handOff.TenantId
        };
    }

    /// <summary>
    /// Whether <paramref name="code"/> is a code of a report: dash-case and at most
    /// <see cref="MaxCodeLength"/> characters.
    /// </summary>
    public static bool IsValidCode(string? code)
    {
        return !string.IsNullOrEmpty(code)
            && code.Length <= MaxCodeLength
            && CodePattern().IsMatch(code);
    }

    [GeneratedRegex("^[a-z][a-z0-9]*(-[a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex CodePattern();
}

/// <summary>
/// What <see cref="INhAssistantProposalExecutionReports.ReportExecutionAsync"/> recorded.
/// </summary>
/// <param name="ApprovalId">The proposal.</param>
/// <param name="Status">Its status after the report: <see cref="NhAssistantApprovalStatuses.Executed"/>,
/// <see cref="NhAssistantApprovalStatuses.ExecutionFailed"/> or <see cref="NhAssistantApprovalStatuses.ExecutionUnknown"/>.</param>
/// <param name="Duplicate">The proposal had already been closed by the same report; nothing changed and
/// nothing was audited, notified or posted again.</param>
public sealed record NhAssistantExecutionReportReceipt(Guid ApprovalId, string Status, bool Duplicate);

/// <summary>
/// The codes of the application's execution of proposals: the outcomes as the audit records them
/// (<see cref="NhAssistantAuditEvent.ExecutionOutcome"/>) and the execution results the assistant sets
/// itself.
/// </summary>
public static class NhAssistantApplicationExecutionCodes
{
    /// <summary>The outcome <see cref="NhAssistantExecutionOutcome.Completed"/>.</summary>
    public const string Completed = "completed";

    /// <summary>The outcome <see cref="NhAssistantExecutionOutcome.NeedsAttention"/>.</summary>
    public const string NeedsAttention = "needs-attention";

    /// <summary>The outcome <see cref="NhAssistantExecutionOutcome.Failed"/>.</summary>
    public const string Failed = "failed";

    /// <summary>
    /// The audit result of a hand-off the application's executor accepted
    /// (<see cref="NhAssistantAuditEventKind.AgentProposalHandedToApplication"/>).
    /// </summary>
    public const string HandedOff = "handed-off";

    /// <summary>
    /// The executor threw, so whether the application took the work is unknown: the proposal's
    /// execution result when it becomes <see cref="NhAssistantApprovalStatuses.ExecutionUnknown"/>.
    /// </summary>
    public const string HandOffFailed = "hand-off-failed";

    /// <summary>
    /// The executor refused the proposal with a failed result that names no dash-case code: the
    /// proposal's execution result when it becomes <see cref="NhAssistantApprovalStatuses.ExecutionFailed"/>.
    /// </summary>
    public const string HandOffRefused = "hand-off-refused";

    /// <summary>
    /// The tool's report deadline passed without a report: the proposal's execution result when the run
    /// maintenance makes it <see cref="NhAssistantApprovalStatuses.ExecutionUnknown"/>.
    /// </summary>
    public const string ReportOverdue = "report-overdue";

    /// <summary>
    /// The audit code of <paramref name="outcome"/>.
    /// </summary>
    public static string Of(NhAssistantExecutionOutcome outcome)
    {
        return outcome switch
        {
            NhAssistantExecutionOutcome.Completed => Completed,
            NhAssistantExecutionOutcome.NeedsAttention => NeedsAttention,
            NhAssistantExecutionOutcome.Failed => Failed,
            _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "An execution completed, needs attention or failed.")
        };
    }

    /// <summary>
    /// The status a proposal gets from a report with <paramref name="outcome"/>.
    /// </summary>
    public static string StatusOf(NhAssistantExecutionOutcome outcome)
    {
        return outcome switch
        {
            NhAssistantExecutionOutcome.Completed => NhAssistantApprovalStatuses.Executed,
            NhAssistantExecutionOutcome.NeedsAttention => NhAssistantApprovalStatuses.ExecutionUnknown,
            NhAssistantExecutionOutcome.Failed => NhAssistantApprovalStatuses.ExecutionFailed,
            _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "An execution completed, needs attention or failed.")
        };
    }
}

/// <summary>
/// A tool whose approved deferred proposals the application executes
/// (<see cref="NhAssistantBuilder.ExecuteInApplication"/>).
/// </summary>
/// <param name="ToolId">The tool's id.</param>
/// <param name="ReportWithin">How long the assistant waits for the application's report after a hand-off.</param>
internal sealed record NhAssistantApplicationExecution(string ToolId, TimeSpan ReportWithin)
{
    /// <summary>
    /// The longest report deadline a tool may declare.
    /// </summary>
    public static readonly TimeSpan MaxReportWithin = TimeSpan.FromDays(90);
}
