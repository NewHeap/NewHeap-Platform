using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NewHeap.Platform.AI.Chat.Collaboration;
using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Chat.Governance;
using NewHeap.Platform.AI.Chat.Live;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.AI.Chat.Runs;
using NewHeap.Platform.AI.Chat.Runtime;
using NewHeap.Platform.Common.Models;

namespace NewHeap.Platform.AI.Chat.AspNet.AgentRuns;

/// <summary>
/// Executes the approved proposals of tools the application executes
/// (<see cref="NhAssistantBuilder.ExecuteInApplication(string, TimeSpan)"/>): hands them to the application's
/// <see cref="INhAssistantProposalExecutor"/>, takes the application's reports
/// (<see cref="INhAssistantProposalExecutionReports"/>) and closes the proposals whose report deadline
/// passed. Every change of a proposal is a compare-and-set transition from
/// <see cref="NhAssistantApprovalStatuses.AwaitingApplication"/>, so a report, a second delivery of it and
/// the deadline close a proposal once. What follows a close is the same for each of them: the work update,
/// the content-free audit, the notification, for a report its summary as an application message in the
/// agent's thread, and the follow-up run when the approval policy asks for one.
/// <para>
/// A person's inline approval of a tool declared with <see cref="NhAssistantApplicationExecutionBuilder.AlsoInline"/>
/// is handed off the same way (LS17). In an agent thread everything above applies. In a person's chat,
/// which belongs to no subject and has no approvers, the audit names no subject, nobody is notified, no
/// follow-up run starts, and the conversation hears every close instead: the outcome with its code, and
/// the summary of a report, as an application message without the trusted flag.
/// </para>
/// </summary>
internal sealed class NhAssistantApplicationExecutions(
    INhAssistantStore store,
    NhAssistantRunStore runs,
    NhAssistantRegistrationState registration,
    NhAssistantAgentEvents events,
    NhAssistantAgentNotifier notifier,
    NhAssistantAgentTriggers triggers,
    NhAssistantAgentWorkUpdates workUpdates,
    NhAssistantLivePublisher live,
    NhAssistantProposalGuards guards,
    NhAssistantProposalInvalidation invalidation,
    NhAssistantApproverCheck approvers,
    IAuthorizationService authorization,
    IOptions<RequestLocalizationOptions> localization,
    IServiceProvider services,
    ILogger<NhAssistantApplicationExecutions> logger,
    NhAssistantTenancy tenancy) : INhAssistantProposalExecutionReports, INhAssistantInlineHandOffs
{
    /// <summary>
    /// The statuses an inline approval is handed off from: the person's decision just approved it.
    /// </summary>
    private static readonly string[] InlineHandOffStatuses = [NhAssistantApprovalStatuses.Approved];

    /// <summary>
    /// The application's clock (a registered <see cref="TimeProvider"/>, otherwise the system clock),
    /// which report deadlines follow, as a proposal's lifetime does.
    /// </summary>
    private readonly TimeProvider _applicationClock = services.GetService<TimeProvider>() ?? TimeProvider.System;

    /// <summary>
    /// Hands the approved proposal to the application's executor after the execution's checks passed:
    /// it waits for the report from now on, until the tool's deadline. A failed result of the executor is
    /// the application's report that it does not execute the proposal; an exception leaves the outcome
    /// unknown. A proposal that another worker moved first is left alone.
    /// </summary>
    public Task<NhAssistantHandOffOutcome?> HandOffAsync(
        AssistantApproval approval,
        AssistantThread thread,
        NhAiProposal proposal,
        NhAssistantApplicationExecution execution,
        IReadOnlyCollection<string> fromStatuses,
        Guid operationId,
        CancellationToken cancellationToken)
    {
        return HandOffCoreAsync(approval, HomeOf(thread), proposal, execution, fromStatuses, operationId, inline: false, cancellationToken);
    }

    /// <summary>
    /// Hands a person's inline approval in their own conversation to the application (LS17), as
    /// <see cref="HandOffAsync"/> does for an approved deferred proposal. The decision ran the proposal
    /// guards already; no background operation carries the hand-off. The approval records the UI culture
    /// of the person's turn that approved it, which the hand-off carries (FB22) and in which a chat later
    /// reads the close of the hand-off (FB4); an agent thread's posts keep the default culture.
    /// </summary>
    public async Task<NhAssistantHandOffOutcome?> HandOffInlineAsync(
        AssistantApproval approval,
        AssistantConversation conversation,
        NhAssistantApplicationExecution execution,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(approval);
        ArgumentNullException.ThrowIfNull(conversation);
        ArgumentNullException.ThrowIfNull(execution);
        var thread = await runs.FindThreadByConversationAsync(conversation.Id, cancellationToken);
        var home = thread is null ? HomeOf(conversation, approval) : HomeOf(thread);
        var proposal = NhAssistantProposalSerializer.Deserialize(approval.ProposalJson);
        return await HandOffCoreAsync(
            approval,
            home,
            proposal,
            execution,
            InlineHandOffStatuses,
            operationId: null,
            inline: true,
            cancellationToken,
            decisionCulture: CultureOfTurn());
    }

    /// <summary>
    /// Resolves an uncertain execution of a person's chat (LS17) with the rules of an agent thread's
    /// <c>resolve</c>: only an <c>execution-unknown</c> proposal, only by the person who approved it in the
    /// chat and who may still approve the tool, <c>hand-to-application</c> for a proposal handed to the
    /// application and <c>mark-resolved</c> for any, never <c>re-execute</c>, because a chat proposal runs
    /// in its function only within the person's turn. Handing it again runs the execution guards first and
    /// starts a new attempt with a new deadline. The audit records <c>AgentProposalResolved</c> with the
    /// resolution. A proposal of an agent thread is resolved through <c>approvals/{id}/resolve</c>.
    /// </summary>
    public async Task<TaskResult<AssistantApproval>> ResolveInChatAsync(
        AssistantConversation conversation,
        Guid approvalId,
        string actorId,
        ClaimsPrincipal user,
        NhAssistantExecutionResolution resolution,
        string? reason,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        ArgumentNullException.ThrowIfNull(user);
        var approval = await store.FindApprovalAsync(approvalId, cancellationToken);
        if (approval is null || approval.ConversationId != conversation.Id || approval.Mode != NhAssistantApprovalModes.Inline)
        {
            return TaskResult<AssistantApproval>.Failed(NhAssistantErrorCodes.ApprovalNotFound, "The proposal was not found.");
        }
        if (await runs.FindThreadByConversationAsync(conversation.Id, cancellationToken) is not null)
        {
            return TaskResult<AssistantApproval>.Failed(
                NhAssistantErrorCodes.ApprovalDecisionInvalid,
                "A proposal of an agent thread is resolved through approvals/{approvalId}/resolve.");
        }
        if (approval.Status != NhAssistantApprovalStatuses.ExecutionUnknown)
        {
            return TaskResult<AssistantApproval>.Failed(
                NhAssistantErrorCodes.ExecutionNotUncertain,
                "Only an execution with an unknown outcome can be resolved.");
        }
        if (!string.Equals(approval.DecidedByActorId, actorId, StringComparison.Ordinal))
        {
            // The person who approved it in their own turn answers for it, as for the decision.
            return TaskResult<AssistantApproval>.Failed(NhAssistantErrorCodes.ApprovalForbidden, "The approval belongs to another participant.");
        }

        var handedToApplication = approval.ExecutionReportDueAt is not null;
        if (resolution == NhAssistantExecutionResolution.ReExecute
            || (resolution == NhAssistantExecutionResolution.HandToApplication && !handedToApplication))
        {
            return TaskResult<AssistantApproval>.Failed(
                NhAssistantErrorCodes.ApprovalDecisionInvalid,
                handedToApplication
                    ? "The application executes this proposal; hand it to the application again."
                    : "Only a proposal the application executes is handed to it again.");
        }

        // The tool's approve policy keeps a person from resolving what they may no longer do themselves.
        foreach (var toolPolicy in approvers.FindDescriptor(approval.ToolId)?.ApproverPolicies ?? [])
        {
            if (!(await authorization.AuthorizeAsync(user, null, toolPolicy)).Succeeded)
            {
                return TaskResult<AssistantApproval>.Failed(NhAssistantErrorCodes.ApprovalPolicyDenied, "The caller may not approve this tool.");
            }
        }

        var home = HomeOf(conversation, approval);
        NhAssistantApplicationExecution? execution = null;
        if (resolution == NhAssistantExecutionResolution.HandToApplication)
        {
            if (!registration.TryGetApplicationExecution(approval.ToolId, out execution))
            {
                return TaskResult<AssistantApproval>.Failed(
                    NhAssistantErrorCodes.ApprovalDecisionInvalid,
                    "The application no longer executes this tool.");
            }

            // The work may have been done another way since: the execution guards decide first.
            if (guards.HasGuards(approval.ToolId, NhAiProposalGuardStages.Execution))
            {
                var check = await guards.CheckAsync(
                    NhAssistantProposalGuards.ContextOf(approval, null),
                    NhAiProposalGuardStages.Execution,
                    cancellationToken);
                if (check.Stale is { } stale)
                {
                    await invalidation.CloseAsync(
                        approval,
                        null,
                        home.AgentId,
                        stale,
                        NhAssistantInvalidationStages.Execution,
                        [NhAssistantApprovalStatuses.ExecutionUnknown],
                        actorId,
                        CancellationToken.None);
                    return TaskResult<AssistantApproval>.Failed(NhAssistantErrorCodes.ProposalStale, "The proposal no longer applies.");
                }
                if (check.Failed)
                {
                    return TaskResult<AssistantApproval>.Failed(
                        NhAssistantErrorCodes.ProposalCheckFailed,
                        "Whether the proposal still applies could not be checked.");
                }
            }
        }

        var resolved = await runs.TryTransitionApprovalAsync(
            approval.Id,
            [NhAssistantApprovalStatuses.ExecutionUnknown],
            execution is not null ? NhAssistantApprovalStatuses.Approved : NhAssistantApprovalStatuses.Resolved,
            new NhAssistantApprovalTransition
            {
                Reason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim(),
                ExecutionResultCode = execution is not null ? "hand-off-acknowledged" : "resolved-by-approver",
                DecidedByActorId = actorId
            },
            cancellationToken);
        if (!resolved)
        {
            return TaskResult<AssistantApproval>.Failed(
                NhAssistantErrorCodes.ExecutionNotUncertain,
                "The execution was resolved by someone else.");
        }

        approval = (await store.FindApprovalAsync(approval.Id, CancellationToken.None))!;
        await events.ProposalAsync(
            NhAssistantAuditEventKind.AgentProposalResolved,
            home.AgentId,
            null,
            approval,
            actorId,
            NhAssistantExecutionResolutions.Of(resolution),
            null,
            null,
            CancellationToken.None);
        if (execution is not null)
        {
            // A new attempt with a new deadline, as an agent thread's hand-off again.
            var proposal = NhAssistantProposalSerializer.Deserialize(approval.ProposalJson);
            await HandOffCoreAsync(approval, home, proposal, execution, InlineHandOffStatuses, operationId: null, inline: true, cancellationToken);
        }

        await live.PublishChangedAsync(conversation.Id, CancellationToken.None);
        return TaskResult<AssistantApproval>.Succeeded((await store.FindApprovalAsync(approval.Id, CancellationToken.None))!);
    }

    public async Task<TaskResult<NhAssistantExecutionReportReceipt>> ReportExecutionAsync(
        NhAssistantExecutionReport report,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentException.ThrowIfNullOrWhiteSpace(report.ProposalHash, nameof(report));
        if (!Enum.IsDefined(report.Outcome))
        {
            throw new ArgumentOutOfRangeException(nameof(report), report.Outcome, "An execution completed, needs attention or failed.");
        }
        if (!NhAssistantExecutionReport.IsValidCode(report.Code))
        {
            throw new ArgumentException(
                $"The code of an execution report is a dash-case code of at most {NhAssistantExecutionReport.MaxCodeLength} characters, such as order-placed.",
                nameof(report));
        }

        // The summary comes from the application's own execution, so a long one is an expected
        // refusal rather than a programming error.
        var summary = string.IsNullOrWhiteSpace(report.Summary) ? null : report.Summary.Trim();
        if (summary is { Length: > NhAssistantExecutionReport.MaxSummaryLength })
        {
            return TaskResult<NhAssistantExecutionReportReceipt>.Failed(
                NhAssistantErrorCodes.ExecutionReportInvalid,
                $"The summary of an execution report has at most {NhAssistantExecutionReport.MaxSummaryLength} characters.");
        }

        var approval = await store.FindApprovalAsync(report.ApprovalId, cancellationToken);
        var home = approval is null ? null : await HomeOfAsync(approval, cancellationToken);
        // The application reports within one tenant: another tenant's proposal, and the whole
        // application's for a tenant that takes no part in it, read as unknown.
        if (approval is null || home is null || !Reaches(home, report.TenantId))
        {
            return TaskResult<NhAssistantExecutionReportReceipt>.Failed(NhAssistantErrorCodes.ApprovalNotFound, "The proposal was not found.");
        }
        if (!string.Equals(approval.ProposalHash, report.ProposalHash, StringComparison.Ordinal))
        {
            return TaskResult<NhAssistantExecutionReportReceipt>.Failed(NhAssistantErrorCodes.ProposalHashMismatch, "The proposal changed.");
        }

        // A report has no request of a person: what it writes uses the application's default culture,
        // as headless runs and executions do.
        using var culture = NhAssistantRunCulture.Use(localization.Value);
        if (approval.Status == NhAssistantApprovalStatuses.AwaitingApplication
            && await CloseReportedAsync(approval, home, report.Outcome, report.Code, summary) is { } closed)
        {
            return TaskResult<NhAssistantExecutionReportReceipt>.Succeeded(ReceiptOf(closed, duplicate: false));
        }

        // Idempotent by state: a proposal this report closed before is the earlier report; any other
        // proposal does not wait for one.
        var latest = await store.FindApprovalAsync(approval.Id, cancellationToken) ?? approval;
        return IsRepeat(latest, report)
            ? TaskResult<NhAssistantExecutionReportReceipt>.Succeeded(ReceiptOf(latest, duplicate: true))
            : TaskResult<NhAssistantExecutionReportReceipt>.Failed(
                NhAssistantErrorCodes.ExecutionReportNotAwaited,
                "The proposal does not wait for a report of its execution.");
    }

    /// <summary>
    /// Makes the proposals whose report deadline <paramref name="now"/> of the application's clock
    /// reached uncertain, with <see cref="NhAssistantApplicationExecutionCodes.ReportOverdue"/>: an
    /// approver hands each to the application again or marks it resolved.
    /// </summary>
    public async Task<int> ExpireReportsAsync(DateTimeOffset now, int maximum, CancellationToken cancellationToken)
    {
        using var culture = NhAssistantRunCulture.Use(localization.Value);
        var expired = 0;
        foreach (var approval in await runs.GetOverdueExecutionReportsAsync(now, maximum, cancellationToken))
        {
            if (await HomeOfAsync(approval, cancellationToken) is not { } home)
            {
                continue;
            }

            if (await CloseUncertainAsync(approval, home, NhAssistantApplicationExecutionCodes.ReportOverdue))
            {
                expired++;
            }
        }

        if (expired > 0)
        {
            logger.LogInformation("{Count} proposals executed by the application passed their report deadline.", expired);
        }

        return expired;
    }

    /// <summary>
    /// Hands the proposal to the executor from <paramref name="fromStatuses"/>, once, and returns how it
    /// ended; <see langword="null"/> when another worker moved the proposal first. A
    /// <paramref name="decisionCulture"/> is recorded with the hand-off; without one the approval keeps
    /// the culture it recorded, as a resolution's new hand-off does.
    /// </summary>
    private async Task<NhAssistantHandOffOutcome?> HandOffCoreAsync(
        AssistantApproval approval,
        ExecutionHome home,
        NhAiProposal proposal,
        NhAssistantApplicationExecution execution,
        IReadOnlyCollection<string> fromStatuses,
        Guid? operationId,
        bool inline,
        CancellationToken cancellationToken,
        string? decisionCulture = null)
    {
        var executor = services.GetService<INhAssistantProposalExecutor>()
            ?? throw new InvalidOperationException(
                $"Tool '{execution.ToolId}' is executed by the application, but no executor is registered. Call UseProposalExecutor.");
        var dueAt = _applicationClock.GetUtcNow().Add(execution.ReportWithin);
        var handed = await runs.TryTransitionApprovalAsync(
            approval.Id,
            fromStatuses,
            NhAssistantApprovalStatuses.AwaitingApplication,
            new NhAssistantApprovalTransition
            {
                ExecutionOperationId = operationId,
                CountExecutionAttempt = true,
                ExecutionReportDueAt = dueAt,
                ClearExecutedAt = true,
                DecisionCulture = decisionCulture
            },
            CancellationToken.None);
        if (!handed)
        {
            return null;
        }

        var current = (await store.FindApprovalAsync(approval.Id, CancellationToken.None))!;
        await PublishAsync(home, current);
        // The hand-off is recorded before the executor runs, so a report the application makes while it
        // takes the work follows it in the audit.
        await events.ProposalAsync(
            NhAssistantAuditEventKind.AgentProposalHandedToApplication,
            home.AgentId,
            home.Thread,
            current,
            NhAssistantAgentEvents.AgentActorPrefix + home.AgentId,
            NhAssistantApplicationExecutionCodes.HandedOff,
            null,
            null,
            CancellationToken.None);

        var handOff = new NhAssistantProposalHandOff(
            current.Id,
            proposal,
            current.DecidedByActorId ?? string.Empty,
            current.DecidedAt ?? current.CreatedAt,
            home.AgentId,
            home.Subject,
            home.TenantId,
            current.ExecutionAttempts,
            dueAt)
        {
            Inline = inline,
            DecisionCulture = current.DecisionCulture,
            // FB51: the policy the proposal waited under, also one a selector chose, as it was recorded.
            PolicyId = current.PolicyId,
            Mode = NhAssistantApprovalModes.ToMode(current.Mode)
        };
        TaskResult result;
        try
        {
            result = await executor.ExecuteAsync(handOff, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The proposal waits for the report; without one its deadline makes it uncertain.
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            logger.LogError(
                exception,
                "The application's executor did not take proposal {ApprovalId} of tool {ToolId}; its outcome is unknown.",
                current.Id,
                current.ToolId);
            await CloseUncertainAsync(current, home, NhAssistantApplicationExecutionCodes.HandOffFailed);
            return new NhAssistantHandOffOutcome(NhAssistantApprovalStatuses.ExecutionUnknown, NhAssistantApplicationExecutionCodes.HandOffFailed, dueAt);
        }

        if (!result.Success)
        {
            var code = NhAssistantResults.CodeOf(result) is { } refused && NhAssistantExecutionReport.IsValidCode(refused)
                ? refused
                : NhAssistantApplicationExecutionCodes.HandOffRefused;
            logger.LogInformation(
                "The application refused to execute proposal {ApprovalId} of tool {ToolId} ({Code}).",
                current.Id,
                current.ToolId,
                code);
            await CloseReportedAsync(current, home, NhAssistantExecutionOutcome.Failed, code, null);
            return new NhAssistantHandOffOutcome(NhAssistantApprovalStatuses.ExecutionFailed, code, dueAt);
        }

        // The executor may have reported at once, when the work was already done.
        var latest = await store.FindApprovalAsync(current.Id, CancellationToken.None) ?? current;
        return new NhAssistantHandOffOutcome(
            latest.Status,
            latest.Status == NhAssistantApprovalStatuses.AwaitingApplication ? null : latest.ExecutionResultCode,
            dueAt);
    }

    /// <summary>
    /// Whether the proposal is closed by the same report: it was handed to the application, a report
    /// ended its execution (the deadline and a failed hand-off end none) with this outcome and code.
    /// </summary>
    private static bool IsRepeat(AssistantApproval approval, NhAssistantExecutionReport report)
    {
        return approval.ExecutionReportDueAt is not null
            && approval.ExecutedAt is not null
            && string.Equals(approval.Status, NhAssistantApplicationExecutionCodes.StatusOf(report.Outcome), StringComparison.Ordinal)
            && string.Equals(approval.ExecutionResultCode, report.Code, StringComparison.Ordinal);
    }

    /// <summary>
    /// The receipt of a report on <paramref name="approval"/>, with the culture the person approved it in.
    /// </summary>
    private static NhAssistantExecutionReportReceipt ReceiptOf(AssistantApproval approval, bool duplicate)
    {
        return new NhAssistantExecutionReportReceipt(approval.Id, approval.Status, duplicate)
        {
            DecisionCulture = approval.DecisionCulture
        };
    }

    /// <summary>
    /// Closes a waiting proposal with the application's report, once; <see langword="null"/> when it no
    /// longer waits.
    /// </summary>
    private async Task<AssistantApproval?> CloseReportedAsync(
        AssistantApproval approval,
        ExecutionHome home,
        NhAssistantExecutionOutcome outcome,
        string code,
        string? summary)
    {
        var status = NhAssistantApplicationExecutionCodes.StatusOf(outcome);
        var closed = await runs.TryTransitionApprovalAsync(
            approval.Id,
            [NhAssistantApprovalStatuses.AwaitingApplication],
            status,
            new NhAssistantApprovalTransition
            {
                ExecutedAt = DateTimeOffset.UtcNow,
                ExecutionResultCode = code
            },
            CancellationToken.None);
        if (!closed)
        {
            return null;
        }

        var current = (await store.FindApprovalAsync(approval.Id, CancellationToken.None))!;
        await PublishAsync(home, current);
        await events.ProposalAsync(
            NhAssistantAuditEventKind.AgentProposalExecuted,
            home.AgentId,
            home.Thread,
            current,
            NhAssistantSpeakers.ApplicationActorId,
            status,
            NhAssistantApplicationExecutionCodes.Of(outcome),
            code,
            CancellationToken.None);
        if (home.Thread is not { } thread)
        {
            // A chat has no approvers to notify: the person reads the outcome in their conversation, in
            // the culture of the turn that approved it.
            using (UseChatCulture(current))
            {
                await PostAsync(current, home, ReportText(outcome, code, summary), "execution-report-");
            }

            return current;
        }

        await notifier.ConsultationProposalEndedAsync(thread, current, CancellationToken.None);
        await notifier.NotifyAsync(
            outcome == NhAssistantExecutionOutcome.NeedsAttention
                ? NhAssistantAgentNotificationKind.ExecutionUnknown
                : NhAssistantAgentNotificationKind.ExecutionReported,
            thread,
            current.RunId,
            current,
            current.InitiatorActorId,
            shadow: false,
            CancellationToken.None,
            failureCode: code);
        if (summary is not null)
        {
            await PostAsync(current, home, NhAssistantMarkdown.Escape(summary), "execution-report-");
        }

        await FollowUpAsync(current, thread);
        return current;
    }

    /// <summary>
    /// Makes a waiting proposal uncertain without a report, once: its executor threw or its deadline
    /// passed. The approvers are told, as for any uncertain execution; in a chat the conversation is.
    /// </summary>
    private async Task<bool> CloseUncertainAsync(AssistantApproval approval, ExecutionHome home, string code)
    {
        var closed = await runs.TryTransitionApprovalAsync(
            approval.Id,
            [NhAssistantApprovalStatuses.AwaitingApplication],
            NhAssistantApprovalStatuses.ExecutionUnknown,
            new NhAssistantApprovalTransition { ExecutionResultCode = code },
            CancellationToken.None);
        if (!closed)
        {
            return false;
        }

        var current = (await store.FindApprovalAsync(approval.Id, CancellationToken.None))!;
        await PublishAsync(home, current);
        await events.ProposalAsync(
            NhAssistantAuditEventKind.AgentProposalExecuted,
            home.AgentId,
            home.Thread,
            current,
            NhAssistantAgentEvents.AgentActorPrefix + home.AgentId,
            NhAssistantApprovalStatuses.ExecutionUnknown,
            null,
            code,
            CancellationToken.None);
        if (home.Thread is not { } thread)
        {
            // The executor's failure and the passed deadline read as the report does.
            using (UseChatCulture(current))
            {
                await PostAsync(current, home, ReportText(NhAssistantExecutionOutcome.NeedsAttention, code, null), "execution-unknown-");
            }

            return true;
        }

        await notifier.NotifyAsync(
            NhAssistantAgentNotificationKind.ExecutionUnknown,
            thread,
            current.RunId,
            current,
            current.InitiatorActorId,
            shadow: false,
            CancellationToken.None,
            failureCode: code);
        await FollowUpAsync(current, thread);
        return true;
    }

    /// <summary>
    /// The culture a person's chat reads the close of its handed-off proposal in (FB4): the UI culture of
    /// the turn that approved it, when the application still supports it, otherwise the application's
    /// default request culture, as for an approval handed off before the culture was recorded. An agent
    /// thread keeps the default culture of reports and deadlines (DN8).
    /// </summary>
    private IDisposable UseChatCulture(AssistantApproval approval)
    {
        return NhAssistantRunCulture.Use(localization.Value, approval.DecisionCulture);
    }

    /// <summary>
    /// The UI culture the current turn works in, as a person's request or service turn set it, when the
    /// application supports it as a UI culture, as a deferred decision records its culture (FB22).
    /// </summary>
    private string? CultureOfTurn()
    {
        return NhAssistantRunCulture.SupportedUiCultureName(localization.Value, CultureInfo.CurrentUICulture.Name);
    }

    /// <summary>
    /// What a person's chat reads about the close of its handed-off proposal: the outcome with its code,
    /// and the application's summary as written, its Markdown escaped.
    /// </summary>
    private static string ReportText(NhAssistantExecutionOutcome outcome, string code, string? summary)
    {
        var dutch = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "nl";
        var escapedCode = NhAssistantMarkdown.Escape(code);
        var sentence = outcome switch
        {
            NhAssistantExecutionOutcome.Completed => dutch
                ? $"De applicatie heeft het goedgekeurde voorstel uitgevoerd ({escapedCode})."
                : $"The application carried out the approved proposal ({escapedCode}).",
            NhAssistantExecutionOutcome.Failed => dutch
                ? $"De applicatie heeft het goedgekeurde voorstel niet uitgevoerd ({escapedCode})."
                : $"The application did not carry out the approved proposal ({escapedCode}).",
            _ => dutch
                ? $"Het is niet bekend of de applicatie het goedgekeurde voorstel heeft uitgevoerd ({escapedCode}). Controleer het voordat iemand het opnieuw vraagt."
                : $"It is not known whether the application carried out the approved proposal ({escapedCode}). Check it before anyone asks again."
        };
        return summary is null ? sentence : sentence + "\n\n" + NhAssistantMarkdown.Escape(summary);
    }

    /// <summary>
    /// Posts <paramref name="text"/> into the proposal's conversation as an application message without
    /// the trusted flag, so its people read it and a model reads it as information from the application,
    /// never as instructions. One message per hand-off and kind; a failed post is logged and changes
    /// nothing about the close. It reports work the application already did, also for a person's chat,
    /// so it is posted while agent work is off as well (FB17).
    /// </summary>
    private async Task PostAsync(AssistantApproval approval, ExecutionHome home, string text, string keyPrefix)
    {
        var dutch = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "nl";
        var message = new NhAssistantApplicationMessage(NhAssistantContent.Bound(text, registration.Limits.MaxMessageChars))
        {
            Title = dutch ? "Uitvoering van een goedgekeurd voorstel" : "Execution of an approved proposal",
            Access = NhAssistantApplicationMessageAccess.WithoutPersonCheck(),
            IdempotencyKey = keyPrefix + approval.Id.ToString("N") + "-" + approval.ExecutionAttempts.ToString(CultureInfo.InvariantCulture)
        };
        var posted = await services.GetRequiredService<NhAssistantConversationMessages>()
            .PostAsync(home.ConversationId, message, agentWork: false, CancellationToken.None);
        if (!posted.Success)
        {
            logger.LogWarning(
                "The outcome of the execution of proposal {ApprovalId} was not posted ({Code}).",
                approval.Id,
                NhAssistantResults.CodeOf(posted, NhAssistantErrorCodes.MessageInvalid));
        }
    }

    private async Task FollowUpAsync(AssistantApproval approval, AssistantThread thread)
    {
        var policy = approval.PolicyJson is null ? null : NhAssistantApprovalPolicySnapshot.Deserialize(approval.PolicyJson);
        if (policy?.StartFollowUpRun == true)
        {
            await triggers.StartFollowUpAsync(thread, approval, CancellationToken.None);
        }
    }

    /// <summary>
    /// Tells the screens that the proposal changed: the agent's work for a thread, the conversation for
    /// a chat.
    /// </summary>
    private async Task PublishAsync(ExecutionHome home, AssistantApproval approval)
    {
        if (home.Thread is not null)
        {
            await workUpdates.PublishAsync(
                approval.ConversationId,
                NhAssistantAgentWorkReasons.Proposal,
                approval.Id,
                approval.RunId,
                CancellationToken.None);
            return;
        }

        await live.PublishChangedAsync(approval.ConversationId, CancellationToken.None);
    }

    /// <summary>
    /// Where the proposal waits: its agent thread, otherwise the person's chat of an inline approval;
    /// <see langword="null"/> for a proposal without either.
    /// </summary>
    private async Task<ExecutionHome?> HomeOfAsync(AssistantApproval approval, CancellationToken cancellationToken)
    {
        if (await runs.FindThreadByConversationAsync(approval.ConversationId, cancellationToken) is { } thread)
        {
            return HomeOf(thread);
        }

        if (approval.Mode != NhAssistantApprovalModes.Inline
            || await store.FindShareableConversationAsync(approval.ConversationId, cancellationToken) is not { } conversation)
        {
            return null;
        }

        return HomeOf(conversation, approval);
    }

    /// <summary>
    /// The application reports within one tenant: for a thread the whole application's rules, for a
    /// chat its own tenant.
    /// </summary>
    private bool Reaches(ExecutionHome home, string? tenantId)
    {
        return home.Thread is { } thread
            ? tenancy.Reaches(thread, tenantId)
            : string.Equals(home.TenantId, tenantId, StringComparison.Ordinal);
    }

    private static ExecutionHome HomeOf(AssistantThread thread)
    {
        return new ExecutionHome(
            thread.ConversationId,
            thread.AgentId,
            thread.TenantId,
            thread,
            new NhAiSubjectRef(thread.SubjectType, thread.SubjectId));
    }

    /// <summary>
    /// A person's chat belongs to no object: the hand-off names the proposal's first target, or the whole
    /// application when the tool names none.
    /// </summary>
    private static ExecutionHome HomeOf(AssistantConversation conversation, AssistantApproval approval)
    {
        var target = NhAssistantProposalSerializer.Deserialize(approval.ProposalJson).Targets.FirstOrDefault();
        var subject = target is not null
                      && NhAiSubjectRef.IsValidType(target.Type)
                      && !string.IsNullOrWhiteSpace(target.Id)
                      && target.Id.Length <= NhAiSubjectRef.MaxIdLength
                      && target.Id.Trim() == target.Id
            ? new NhAiSubjectRef(target.Type, target.Id)
            : NhAiSystemSubjects.Global;
        return new ExecutionHome(conversation.Id, conversation.AgentId, conversation.TenantId, null, subject);
    }

    /// <summary>
    /// Where a proposal the application executes waits: the agent's thread, or the person's own chat for
    /// an inline approval (<see cref="Thread"/> is <see langword="null"/>).
    /// </summary>
    private sealed record ExecutionHome(
        Guid ConversationId,
        string AgentId,
        string? TenantId,
        AssistantThread? Thread,
        NhAiSubjectRef Subject);
}
