using Microsoft.Extensions.Logging;
using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.AI.Chat.Runtime;
using NewHeap.Platform.Common.Models;

namespace NewHeap.Platform.AI.Chat.Runs;

/// <summary>
/// Decides whether the asking person may see what a tool returned under the consulted agent's own
/// grant or through a delegated read. The ASP.NET package evaluates the policies for the asker
/// against the subject.
/// </summary>
internal interface INhAssistantConsultationViewer
{
    ValueTask<bool> MayViewAsync(
        NhAiGrantSource grantSource,
        string toolId,
        string? argumentsJson,
        CancellationToken cancellationToken);
}

/// <summary>
/// One consultation as the generated tool asks for it.
/// </summary>
/// <param name="AskerActorId">The person whose rights the consultation uses.</param>
/// <param name="AskerInRequest">The asker's own HTTP request is the current request.</param>
internal sealed record NhAssistantConsultationRequest(
    NhAssistantConsultationOptions Options,
    string SubjectId,
    string Question,
    string AskerActorId,
    bool AskerInRequest,
    string Language,
    INhAssistantConsultationViewer Viewer);

/// <summary>
/// The consultation side of a consult turn: who asked through which agents, how deep the chain is
/// and what the consulted agent may do.
/// </summary>
/// <param name="Chain">The agents that asked, outermost first, followed by the consulted agent.</param>
/// <param name="AskingAgentId">The agent whose turn consulted, when an agent turn did.</param>
internal sealed record NhAssistantConsultationScope(
    Guid ConsultationId,
    NhAssistantConsultationOptions Options,
    IReadOnlyList<string> Chain,
    int Depth,
    bool MayPropose,
    string? AskingAgentId,
    INhAssistantConsultationViewer Viewer)
{
    /// <summary>
    /// Whether the consulted agent may in turn be offered the consultation tool
    /// <paramref name="toolId"/>: requests never, consultations only below the depth limit and never
    /// of an agent already in the chain.
    /// </summary>
    public bool MayOffer(string toolId, int maxDepth)
    {
        if (toolId.StartsWith(NhAssistantConsultationTools.RequestPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        return Depth < maxDepth
               && NhAssistantConsultationTools.TargetAgentOf(toolId) is { } agentId
               && !Chain.Contains(agentId, StringComparer.Ordinal);
    }
}

internal sealed record NhAssistantConsultationTurnRequest(
    NhAssistantAgentTurnContext AgentTurn,
    NhAssistantConsultationScope Consultation,
    string Question,
    TimeSpan Timeout,
    string Language);

/// <param name="Outcome">One of <see cref="NhAssistantTurnStatuses"/>.</param>
/// <param name="ProposalCount">The proposals the consulted agent left in its thread.</param>
internal sealed record NhAssistantConsultationTurnResult(
    string Outcome,
    string? ErrorCode,
    string Answer,
    int ToolCalls,
    int InputTokens,
    int OutputTokens,
    int ProposalCount);

/// <summary>
/// Runs consultations: it checks the depth of the chain, the asker's access to the subject and the
/// budgets, records the consultation in the agent's subject thread and runs one bounded turn of the
/// agent with the asker's rights. Refusals are failed results with stable codes, which the asking
/// model reads as the tool result.
/// </summary>
internal sealed class NhAssistantConsultationService(
    NhAssistantRegistrationState registration,
    NhAssistantAgentCatalog agents,
    NhAssistantSubjectAccessService subjects,
    NhAssistantThreadService threads,
    NhAssistantRunStore runs,
    INhAssistantTurnRunner runner,
    NhAssistantAgentNotifier notifier,
    NhAssistantAgentEvents events,
    ILogger<NhAssistantConsultationService> logger)
{
    /// <summary>
    /// Time kept back for the asking turn to use the answer after the consultation returns.
    /// </summary>
    private static readonly TimeSpan AnswerMargin = TimeSpan.FromSeconds(5);

    public async Task<TaskResult<NhAssistantConsultationAnswer>> ConsultAsync(
        NhAssistantConsultationRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var options = request.Options;
        var asking = NhAssistantExecutionScope.CurrentTurn;
        var askingAgentId = asking?.Agent.Id;
        var askers = asking?.Consultation?.Chain ?? (askingAgentId is null ? [] : [askingAgentId]);
        var depth = (asking?.Consultation?.Depth ?? 0) + 1;

        var prepared = await PrepareAsync(request, askers, depth, cancellationToken);
        if (!prepared.Success)
        {
            var code = FirstCode(prepared, NhAssistantErrorCodes.ConsultFailed);
            await events.ConsultationAsync(
                new NhAssistantConsultationEvent(options.AgentId, request.AskerActorId, askingAgentId, depth, code)
                {
                    SubjectType = options.SubjectType
                },
                CancellationToken.None);
            return TaskResult<NhAssistantConsultationAnswer>.Failed(prepared);
        }

        var target = prepared.Data!;
        var consultation = new AssistantConsultation
        {
            Id = Guid.NewGuid(),
            ConversationId = target.Thread.ConversationId,
            ScopeKey = target.Thread.ScopeKey,
            AgentId = options.AgentId,
            SubjectType = target.Thread.SubjectType,
            SubjectId = target.Thread.SubjectId,
            AskerActorId = request.AskerActorId,
            AskingAgentId = askingAgentId,
            AskingConversationId = asking?.ConversationId,
            AskingTurnId = asking?.TurnId,
            Depth = depth,
            Status = NhAssistantConsultationStatuses.Running,
            CreatedAt = DateTimeOffset.UtcNow
        };
        await runs.AddConsultationAsync(consultation, cancellationToken);

        var scope = new NhAssistantConsultationScope(
            consultation.Id,
            options,
            askers.Append(options.AgentId).ToArray(),
            depth,
            options.AllowProposals && target.Access.Has(options.ProposalRole),
            askingAgentId,
            request.Viewer);
        var agentTurn = new NhAssistantAgentTurnContext(
            target.Thread,
            target.Subject,
            target.RunOptions,
            null,
            NhAssistantTriggerIds.Consultation,
            request.AskerActorId,
            request.AskerActorId,
            request.AskerInRequest);

        NhAssistantConsultationTurnResult result;
        try
        {
            result = await runner.RunConsultationTurnAsync(
                new NhAssistantConsultationTurnRequest(agentTurn, scope, request.Question, target.Timeout, request.Language),
                cancellationToken);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            await EndAsync(consultation, target.Thread, null, NhAssistantErrorCodes.ConsultFailed);
            if (exception is OperationCanceledException)
            {
                throw;
            }

            // Model and tool exceptions can carry content; log the type only.
            logger.LogWarning(
                "Consultation {ConsultationId} of agent {AgentId} failed with {ExceptionType}.",
                consultation.Id,
                options.AgentId,
                exception.GetType().Name);
            return TaskResult<NhAssistantConsultationAnswer>.Failed(
                NhAssistantErrorCodes.ConsultFailed,
                "The agent could not answer.");
        }

        var failureCode = result.Outcome == NhAssistantTurnStatuses.Completed
            ? null
            : result.ErrorCode ?? NhAssistantErrorCodes.ConsultFailed;
        await EndAsync(consultation, target.Thread, result, failureCode);
        await NotifyProposalsAsync(target.Thread, consultation);
        if (failureCode is not null)
        {
            return TaskResult<NhAssistantConsultationAnswer>.Failed(failureCode, "The agent could not answer.");
        }

        return TaskResult<NhAssistantConsultationAnswer>.Succeeded(new NhAssistantConsultationAnswer(
            consultation.Id,
            NhAssistantContent.Bound(result.Answer.Trim(), options.MaxAnswerChars),
            result.ProposalCount));
    }

    /// <summary>
    /// Checks everything that can refuse a consultation before it costs anything, and finds or
    /// creates the agent's thread about the subject, which holds the consultation record.
    /// </summary>
    private async Task<TaskResult<NhAssistantConsultationTarget>> PrepareAsync(
        NhAssistantConsultationRequest request,
        IReadOnlyList<string> askers,
        int depth,
        CancellationToken cancellationToken)
    {
        var options = request.Options;
        if (depth > registration.Limits.MaxConsultationDepth || askers.Contains(options.AgentId, StringComparer.Ordinal))
        {
            return Refused(
                NhAssistantErrorCodes.ConsultDepthExceeded,
                "The consultation would nest deeper than allowed or ask an agent that is already asking.");
        }

        var timeout = Timeout(options, NhAssistantExecutionScope.CurrentTurn);
        if (timeout < TimeSpan.FromSeconds(1))
        {
            return Refused(NhAssistantErrorCodes.ConsultTimeout, "No time is left in this turn to consult the agent.");
        }

        if (!registration.AgentRuns.TryGetValue(options.AgentId, out var runOptions)
            || !runOptions.Enabled
            || runOptions.Shadow)
        {
            return Refused(NhAssistantErrorCodes.ConsultAgentUnavailable, "The agent cannot be consulted.");
        }

        var agent = await agents.FindAsync(options.AgentId, includeDisabled: false, cancellationToken);
        if (agent is null)
        {
            return Refused(NhAssistantErrorCodes.ConsultAgentUnavailable, "The agent cannot be consulted.");
        }

        // An unknown subject and a subject the asker may not read look the same to the model.
        var subjectId = request.SubjectId.Trim();
        if (subjectId.Length is 0 or > NhAiSubjectRef.MaxIdLength)
        {
            return SubjectUnavailable();
        }

        var subjectRef = new NhAiSubjectRef(options.SubjectType, subjectId);
        var subject = await subjects.DescribeAsync(subjectRef, cancellationToken);
        if (subject is null)
        {
            return SubjectUnavailable();
        }

        var access = await subjects.ResolveAsync(subjectRef, request.AskerActorId, cancellationToken);
        if (!access.Has(options.MinimumRole))
        {
            return SubjectUnavailable();
        }

        var since = new DateTimeOffset(DateTimeOffset.UtcNow.UtcDateTime.Date, TimeSpan.Zero);
        if (await runs.CountConsultationsSinceAsync(request.AskerActorId, since, cancellationToken)
            >= registration.Limits.MaxConsultationsPerActorPerDay)
        {
            return Refused(NhAssistantErrorCodes.ConsultLimitReached, "The daily number of consultations is reached.");
        }

        if (runOptions.DailyBudget is { } budget)
        {
            var usage = await runs.GetAgentUsageSinceAsync(options.AgentId, subject.ScopeKey, since, cancellationToken);
            if (usage.ToolCalls >= budget.ToolCalls || usage.Tokens >= budget.Tokens)
            {
                return Refused(NhAssistantErrorCodes.ConsultBudgetExhausted, "The agent's daily budget is spent.");
            }
        }

        var thread = await threads.GetOrCreateAsync(agent, subject, cancellationToken);
        if (!thread.Success)
        {
            return Refused(NhAssistantErrorCodes.ConsultAgentUnavailable, "The agent cannot be consulted about this subject.");
        }
        if (thread.Data!.MembersRefreshedAt is null)
        {
            // A thread a consultation created has no member snapshot yet; listings, names and
            // notifications of its proposals read it. Access never depends on it, so a failed
            // refresh does not stop the consultation.
            var refreshed = await subjects.RefreshAsync(subjectRef, cancellationToken);
            if (!refreshed.Success)
            {
                logger.LogInformation(
                    "The member snapshot of subject type {SubjectType} could not be refreshed for a consultation of agent {AgentId}.",
                    subjectRef.Type,
                    options.AgentId);
            }
        }

        return TaskResult<NhAssistantConsultationTarget>.Succeeded(
            new NhAssistantConsultationTarget(thread.Data!, subject, access, runOptions, timeout));
    }

    /// <summary>
    /// The consultation's own timeout, capped by what is left of the asking turn.
    /// </summary>
    private static TimeSpan Timeout(NhAssistantConsultationOptions options, NhAssistantTurnScope? asking)
    {
        if (asking is null)
        {
            return options.Timeout;
        }

        var left = asking.Deadline - DateTimeOffset.UtcNow - AnswerMargin;
        return left < options.Timeout ? left : options.Timeout;
    }

    private async Task EndAsync(
        AssistantConsultation consultation,
        AssistantThread thread,
        NhAssistantConsultationTurnResult? result,
        string? failureCode)
    {
        var status = failureCode is null ? NhAssistantConsultationStatuses.Completed : NhAssistantConsultationStatuses.Failed;
        var ended = await runs.EndConsultationAsync(
            consultation.Id,
            status,
            failureCode,
            result?.ToolCalls ?? 0,
            result?.InputTokens ?? 0,
            result?.OutputTokens ?? 0,
            result?.ProposalCount ?? 0,
            CancellationToken.None);
        if (!ended)
        {
            return;
        }

        await events.ConsultationAsync(
            new NhAssistantConsultationEvent(
                consultation.AgentId,
                consultation.AskerActorId,
                consultation.AskingAgentId,
                consultation.Depth,
                failureCode ?? status)
            {
                ConversationId = thread.ConversationId,
                ConsultationId = consultation.Id,
                SubjectType = thread.SubjectType,
                SubjectId = thread.SubjectId,
                ToolCalls = result?.ToolCalls,
                InputTokens = result?.InputTokens,
                OutputTokens = result?.OutputTokens,
                ProposalCount = result?.ProposalCount
            },
            CancellationToken.None);
    }

    /// <summary>
    /// Tells the subject's approvers about the proposals a consultation left, as after a run.
    /// </summary>
    private async Task NotifyProposalsAsync(AssistantThread thread, AssistantConsultation consultation)
    {
        var waiting = (await runs.GetConsultationApprovalsAsync(consultation.Id, CancellationToken.None))
            .Where(approval => approval.Status == NhAssistantApprovalStatuses.Pending)
            .ToArray();
        if (waiting.Length == 0)
        {
            return;
        }

        await notifier.NotifyAsync(
            NhAssistantAgentNotificationKind.RunEndedWithProposals,
            thread,
            null,
            waiting[0],
            consultation.AskerActorId,
            false,
            CancellationToken.None,
            proposalCount: waiting.Length);
    }

    private static TaskResult<NhAssistantConsultationTarget> SubjectUnavailable()
    {
        return Refused(
            NhAssistantErrorCodes.ConsultSubjectUnavailable,
            "The subject does not exist or you may not consult the agent about it.");
    }

    private static TaskResult<NhAssistantConsultationTarget> Refused(string code, string message)
    {
        return TaskResult<NhAssistantConsultationTarget>.Failed(code, message);
    }

    private static string FirstCode(TaskResult result, string fallback)
    {
        return result.GetResultItems()
            .Select(item => item.Name)
            .FirstOrDefault(name => !string.IsNullOrWhiteSpace(name))
            ?? fallback;
    }

    private sealed record NhAssistantConsultationTarget(
        AssistantThread Thread,
        NhAiSubjectInfo Subject,
        NhAiSubjectAccess Access,
        NhAssistantAgentRunOptions RunOptions,
        TimeSpan Timeout);
}

/// <summary>
/// The facts of one consultation audit event.
/// </summary>
internal sealed record NhAssistantConsultationEvent(
    string AgentId,
    string AskerActorId,
    string? AskingAgentId,
    int Depth,
    string ResultCode)
{
    public Guid ConversationId { get; init; }

    public Guid? ConsultationId { get; init; }

    public string? SubjectType { get; init; }

    public string? SubjectId { get; init; }

    public int? ToolCalls { get; init; }

    public long? InputTokens { get; init; }

    public long? OutputTokens { get; init; }

    public int? ProposalCount { get; init; }
}
