using System.Globalization;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NewHeap.Platform.AI.Chat.Collaboration;
using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Chat.Live;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.AI.Chat.Runs;
using NewHeap.Platform.AI.Chat.Runtime;
using NewHeap.Platform.Common.Models;

namespace NewHeap.Platform.AI.Chat.AspNet.AgentRuns;

/// <summary>
/// Executes the approved proposals of tools the application executes
/// (<see cref="NhAssistantBuilder.ExecuteInApplication"/>): hands them to the application's
/// <see cref="INhAssistantProposalExecutor"/>, takes the application's reports
/// (<see cref="INhAssistantProposalExecutionReports"/>) and closes the proposals whose report deadline
/// passed. Every change of a proposal is a compare-and-set transition from
/// <see cref="NhAssistantApprovalStatuses.AwaitingApplication"/>, so a report, a second delivery of it and
/// the deadline close a proposal once. What follows a close is the same for each of them: the work update,
/// the content-free audit, the notification, for a report its summary as an application message in the
/// agent's thread, and the follow-up run when the approval policy asks for one.
/// </summary>
internal sealed class NhAssistantApplicationExecutions(
    INhAssistantStore store,
    NhAssistantRunStore runs,
    NhAssistantRegistrationState registration,
    NhAssistantAgentEvents events,
    NhAssistantAgentNotifier notifier,
    NhAssistantAgentTriggers triggers,
    NhAssistantAgentWorkUpdates workUpdates,
    IOptions<RequestLocalizationOptions> localization,
    IServiceProvider services,
    ILogger<NhAssistantApplicationExecutions> logger) : INhAssistantProposalExecutionReports
{
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
    public async Task HandOffAsync(
        AssistantApproval approval,
        AssistantThread thread,
        NhAiProposal proposal,
        NhAssistantApplicationExecution execution,
        IReadOnlyCollection<string> fromStatuses,
        Guid operationId,
        CancellationToken cancellationToken)
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
                ClearExecutedAt = true
            },
            CancellationToken.None);
        if (!handed)
        {
            return;
        }

        var current = (await store.FindApprovalAsync(approval.Id, CancellationToken.None))!;
        await workUpdates.PublishAsync(
            current.ConversationId,
            NhAssistantAgentWorkReasons.Proposal,
            current.Id,
            current.RunId,
            CancellationToken.None);
        // The hand-off is recorded before the executor runs, so a report the application makes while it
        // takes the work follows it in the audit.
        await events.ProposalAsync(
            NhAssistantAuditEventKind.AgentProposalHandedToApplication,
            thread,
            current,
            NhAssistantAgentEvents.AgentActorPrefix + thread.AgentId,
            NhAssistantApplicationExecutionCodes.HandedOff,
            CancellationToken.None);

        var handOff = new NhAssistantProposalHandOff(
            current.Id,
            proposal,
            current.DecidedByActorId ?? string.Empty,
            current.DecidedAt ?? current.CreatedAt,
            thread.AgentId,
            new NhAiSubjectRef(thread.SubjectType, thread.SubjectId),
            thread.TenantId,
            current.ExecutionAttempts,
            dueAt);
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
            await CloseUncertainAsync(current, thread, NhAssistantApplicationExecutionCodes.HandOffFailed);
            return;
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
            await CloseReportedAsync(current, thread, NhAssistantExecutionOutcome.Failed, code, null);
        }
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
        var thread = approval is null ? null : await runs.FindThreadByConversationAsync(approval.ConversationId, cancellationToken);
        // The application reports within one tenant: another tenant's proposal, and the whole
        // application's for a tenant that takes no part in it, read as unknown.
        if (approval is null
            || thread is null
            || !NhAssistantWholeApplicationTenancy.Reaches(registration, thread, report.TenantId))
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
            && await CloseReportedAsync(approval, thread, report.Outcome, report.Code, summary) is { } closed)
        {
            return TaskResult<NhAssistantExecutionReportReceipt>.Succeeded(
                new NhAssistantExecutionReportReceipt(closed.Id, closed.Status, Duplicate: false));
        }

        // Idempotent by state: a proposal this report closed before is the earlier report; any other
        // proposal does not wait for one.
        var latest = await store.FindApprovalAsync(approval.Id, cancellationToken) ?? approval;
        return IsRepeat(latest, report)
            ? TaskResult<NhAssistantExecutionReportReceipt>.Succeeded(new NhAssistantExecutionReportReceipt(latest.Id, latest.Status, Duplicate: true))
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
            if (await runs.FindThreadByConversationAsync(approval.ConversationId, cancellationToken) is not { } thread)
            {
                continue;
            }

            if (await CloseUncertainAsync(approval, thread, NhAssistantApplicationExecutionCodes.ReportOverdue))
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
    /// Closes a waiting proposal with the application's report, once; <see langword="null"/> when it no
    /// longer waits.
    /// </summary>
    private async Task<AssistantApproval?> CloseReportedAsync(
        AssistantApproval approval,
        AssistantThread thread,
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
        await workUpdates.PublishAsync(
            current.ConversationId,
            NhAssistantAgentWorkReasons.Proposal,
            current.Id,
            current.RunId,
            CancellationToken.None);
        await events.ProposalAsync(
            NhAssistantAuditEventKind.AgentProposalExecuted,
            thread,
            current,
            NhAssistantSpeakers.ApplicationActorId,
            status,
            NhAssistantApplicationExecutionCodes.Of(outcome),
            code,
            CancellationToken.None);
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
            await PostSummaryAsync(current, thread, summary);
        }

        await FollowUpAsync(current, thread);
        return current;
    }

    /// <summary>
    /// Makes a waiting proposal uncertain without a report, once: its executor threw or its deadline
    /// passed. The approvers are told, as for any uncertain execution.
    /// </summary>
    private async Task<bool> CloseUncertainAsync(AssistantApproval approval, AssistantThread thread, string code)
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
        await workUpdates.PublishAsync(
            current.ConversationId,
            NhAssistantAgentWorkReasons.Proposal,
            current.Id,
            current.RunId,
            CancellationToken.None);
        await events.ProposalAsync(
            NhAssistantAuditEventKind.AgentProposalExecuted,
            thread,
            current,
            NhAssistantAgentEvents.AgentActorPrefix + thread.AgentId,
            NhAssistantApprovalStatuses.ExecutionUnknown,
            null,
            code,
            CancellationToken.None);
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
    /// Posts the application's summary into the agent's thread as an application message without the
    /// trusted flag, so the people of the thread read it and the follow-up run reads it as information
    /// from the application, never as instructions. Its Markdown is escaped, so it shows as written. One
    /// message per hand-off; a failed post is logged and changes nothing about the report.
    /// </summary>
    private async Task PostSummaryAsync(AssistantApproval approval, AssistantThread thread, string summary)
    {
        var dutch = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "nl";
        var text = NhAssistantContent.Bound(NhAssistantMarkdown.Escape(summary), registration.Limits.MaxMessageChars);
        var message = new NhAssistantApplicationMessage(text)
        {
            Title = dutch ? "Uitvoering van een goedgekeurd voorstel" : "Execution of an approved proposal",
            Access = NhAssistantApplicationMessageAccess.WithoutPersonCheck(),
            IdempotencyKey = "execution-report-" + approval.Id.ToString("N") + "-" + approval.ExecutionAttempts.ToString(CultureInfo.InvariantCulture)
        };
        var posted = await services.GetRequiredService<INhAssistantConversationMessages>()
            .PostAsync(thread.ConversationId, message, CancellationToken.None);
        if (!posted.Success)
        {
            logger.LogWarning(
                "The summary of the execution of proposal {ApprovalId} was not posted ({Code}).",
                approval.Id,
                NhAssistantResults.CodeOf(posted, NhAssistantErrorCodes.MessageInvalid));
        }
    }

    private async Task FollowUpAsync(AssistantApproval approval, AssistantThread thread)
    {
        var policy = approval.PolicyJson is null ? null : NhAssistantApprovalPolicySnapshot.Deserialize(approval.PolicyJson);
        if (policy?.StartFollowUpRun == true)
        {
            await triggers.StartFollowUpAsync(thread, approval, approval.DecidedByActorId, CancellationToken.None);
        }
    }
}
