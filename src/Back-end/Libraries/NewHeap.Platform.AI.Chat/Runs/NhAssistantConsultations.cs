using Microsoft.Extensions.Logging;
using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.AI.Chat.Runtime;
using NewHeap.Platform.Common.Models;

namespace NewHeap.Platform.AI.Chat.Runs;

/// <summary>
/// Decides whether the asking person may see what a tool returned under the consulted agent's own
/// grant or through a delegated read. The ASP.NET package evaluates the tool's policies for the
/// asker against the subject; a delegated read only the person it read for may see.
/// </summary>
internal interface INhAssistantConsultationViewer
{
    ValueTask<bool> MayViewAsync(
        NhAiGrantSource grantSource,
        string toolId,
        string? readForActorId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Whether the asking person may decide a proposal of <paramref name="toolId"/> in the thread under
    /// <paramref name="policy"/> (CP4): an approver rule they meet, the initiator rule included, and the
    /// tool's approve policies, checked with the live principal of their own request. Without that
    /// request nobody decides here.
    /// </summary>
    ValueTask<bool> MayDecideAsync(
        NhAiApprovalPolicy policy,
        AssistantThread thread,
        string toolId,
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
    /// The routes of the consultation's proposals as they apply now: the registration within the
    /// administration's switches.
    /// </summary>
    public NhAssistantConsultationRouting Routing { get; init; } = new(false, NhAssistantConsultationRights.Asker);

    /// <summary>
    /// The person whose rights the consultation uses.
    /// </summary>
    public string AskerActorId { get; init; } = string.Empty;

    /// <summary>
    /// The asking turn that can wait for its person's decision (CP9): a person's own turn in their own
    /// request, not a nested consultation; <see langword="null"/> otherwise, such as for an MCP call or
    /// a headless run.
    /// </summary>
    public NhAssistantConsultationAsker? Asker { get; init; }

    /// <summary>
    /// The proposals the consult turn recorded, by route.
    /// </summary>
    public NhAssistantConsultationOutcome Outcome { get; } = new();

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

/// <summary>
/// The asking turn of a consultation that waits for its person to decide: its scope, which holds the
/// one decision a turn may wait for, and the consult call that waits.
/// </summary>
internal sealed record NhAssistantConsultationAsker(NhAssistantTurnScope AskingTurn, NhAssistantToolCallScope AskingCall);

/// <summary>
/// What a consult turn proposed, as it recorded it: the proposal the asker decides, if any, and every
/// proposal with its route. Only the consult turn's flow writes it.
/// </summary>
internal sealed class NhAssistantConsultationOutcome
{
    private readonly object _sync = new();
    private readonly List<NhAssistantConsultationProposal> _proposals = [];
    private int _askerDecisionReserved;

    /// <summary>
    /// Reserves the one proposal of the consultation the asker decides (CP7).
    /// </summary>
    public bool TryReserveAskerDecision()
    {
        return Interlocked.Exchange(ref _askerDecisionReserved, 1) == 0;
    }

    public void ReleaseAskerDecision()
    {
        Volatile.Write(ref _askerDecisionReserved, 0);
    }

    public void Record(Guid approvalId, string route)
    {
        lock (_sync)
        {
            _proposals.Add(new NhAssistantConsultationProposal(approvalId, route));
        }
    }

    public IReadOnlyList<NhAssistantConsultationProposal> Proposals
    {
        get
        {
            lock (_sync)
            {
                return _proposals.ToArray();
            }
        }
    }

    /// <summary>
    /// The proposal the asker decides, when the consult turn recorded one.
    /// </summary>
    public Guid? AskerApprovalId => Proposals
        .Where(proposal => proposal.Route == NhAssistantConsultationRoutes.AskerDecides)
        .Select(proposal => (Guid?)proposal.ApprovalId)
        .FirstOrDefault();
}

/// <param name="Route">One of <see cref="NhAssistantConsultationRoutes"/>.</param>
internal sealed record NhAssistantConsultationProposal(Guid ApprovalId, string Route);

/// <summary>
/// The proposal of a consultation that the asking turn's person decides in their own conversation,
/// with what the approval card shows about its origin.
/// </summary>
/// <param name="LeaveRoute">
/// The route at the subject the person may leave it for, one of <see cref="NhAssistantConsultationRoutes"/>,
/// or <see langword="null"/> when none applies.
/// </param>
internal sealed record NhAssistantAskerDecision(
    Guid ApprovalId,
    Guid ConsultationId,
    string AgentId,
    NhAiSubjectRef Subject,
    string? SubjectDisplayName,
    string? LeaveRoute);

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
    ILogger<NhAssistantConsultationService> logger,
    NhAssistantWorksOnRule worksOn)
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
        var askingCall = NhAssistantExecutionScope.CurrentCall;
        var askingScope = NhAiAgentInvocationScopes.Current;
        // Only a person's own turn in their own request can wait for them to decide (CP9): not an MCP
        // call, a headless run or a nested consultation.
        var asker = asking is not null
                    && askingCall is not null
                    && asking.Consultation is null
                    && request.AskerInRequest
                    && string.Equals(asking.OwnerActorId, request.AskerActorId, StringComparison.Ordinal)
                    && (askingScope is null || askingScope.IsRequestTurn)
            ? new NhAssistantConsultationAsker(asking, askingCall)
            : null;

        var prepared = await PrepareAsync(request, askers, depth, cancellationToken);
        if (!prepared.Success)
        {
            var code = NhAssistantResults.CodeOf(prepared, NhAssistantErrorCodes.ConsultFailed);
            await events.ConsultationAsync(
                new NhAssistantConsultationEvent(options.AgentId, request.AskerActorId, askingAgentId, depth, code)
                {
                    SubjectType = options.SubjectType,
                    ConsultationSource = options.Source,
                    ConsultationVersion = options.DataVersion
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
            // The version of a consultation defined as data it runs under; it finishes with these
            // options, also when an administrator changes the consultation meanwhile.
            ConsultationVersion = options.DataVersion,
            Status = NhAssistantConsultationStatuses.Running,
            CreatedAt = DateTimeOffset.UtcNow
        };
        await runs.AddConsultationAsync(consultation, cancellationToken);

        // The administration switches routes within what the registration allows (CP21).
        var routing = NhAssistantConsultationRouting.Of(options, target.Agent.ConsultationRoutes);
        var mayPropose = target.Access.Has(options.ProposalRole)
                         && (routing.AtSubjectRights is not null || (routing.AskerDecides && asker is not null));
        var scope = new NhAssistantConsultationScope(
            consultation.Id,
            options,
            askers.Append(options.AgentId).ToArray(),
            depth,
            mayPropose,
            askingAgentId,
            request.Viewer)
        {
            Routing = routing,
            AskerActorId = request.AskerActorId,
            Asker = asker
        };
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
            await WithdrawAskerDecisionAsync(scope);
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
            // Nobody waits for a decision of a consultation that failed.
            await WithdrawAskerDecisionAsync(scope);
            return TaskResult<NhAssistantConsultationAnswer>.Failed(failureCode, "The agent could not answer.");
        }

        var proposals = scope.Outcome.Proposals;
        var askerApprovalId = scope.Outcome.AskerApprovalId;
        if (askerApprovalId is { } waiting && asker is not null)
        {
            // The asking turn waits for its person's decision once this call returns (option A).
            asker.AskingCall.AskerDecision = new NhAssistantAskerDecision(
                waiting,
                consultation.Id,
                options.AgentId,
                target.Subject.Subject,
                target.Thread.SubjectDisplayName ?? target.Subject.DisplayName,
                NhAssistantConsultationProposals.LeaveRouteOf(routing, target.Thread, target.Agent));
        }

        return TaskResult<NhAssistantConsultationAnswer>.Succeeded(new NhAssistantConsultationAnswer(
            consultation.Id,
            NhAssistantContent.Bound(result.Answer.Trim(), options.MaxAnswerChars),
            result.ProposalCount)
        {
            Decision = askerApprovalId is not null
                ? NhAssistantConsultationDecisions.Asker
                : proposals.Count > 0 ? NhAssistantConsultationDecisions.Subject : null,
            ApprovalIds = proposals
                .OrderBy(proposal => proposal.Route == NhAssistantConsultationRoutes.AskerDecides ? 0 : 1)
                .Select(proposal => proposal.ApprovalId)
                .ToArray()
        });
    }

    /// <summary>
    /// Withdraws the proposal a failed consultation left for its asker, and frees the asking turn's
    /// one decision.
    /// </summary>
    private async Task WithdrawAskerDecisionAsync(NhAssistantConsultationScope scope)
    {
        if (scope.Outcome.AskerApprovalId is not { } approvalId)
        {
            return;
        }

        await runs.TryTransitionApprovalAsync(
            approvalId,
            [NhAssistantApprovalStatuses.Pending],
            NhAssistantApprovalStatuses.Withdrawn,
            NhAssistantApprovalTransition.None,
            CancellationToken.None);
        scope.Asker?.AskingTurn.ReleaseAskerDecision();
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
        // The asking turn's limit is checked first, so every consultation it starts counts (LS22).
        var asking = NhAssistantExecutionScope.CurrentTurn;
        if (asking is not null
            && TurnLimitOf(asking) is { } turnLimit
            && asking.IncrementConsultations() > turnLimit)
        {
            return Refused(NhAssistantErrorCodes.ConsultTurnLimit, "This turn reached its number of consultations.");
        }

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
            || !runOptions.Enabled)
        {
            return Refused(NhAssistantErrorCodes.ConsultAgentUnavailable, "The agent cannot be consulted.");
        }

        // An agent in shadow answers nobody: its work stays with administrators.
        var agent = await agents.FindAsync(options.AgentId, includeDisabled: false, cancellationToken);
        if (agent is null || NhAssistantShadowModes.IsAgentShadow(runOptions, agent.ShadowOverride))
        {
            return Refused(NhAssistantErrorCodes.ConsultAgentUnavailable, "The agent cannot be consulted.");
        }

        // An agent is consulted only about what it works on; startup checks the declarations too.
        if (worksOn.RefusalOf(options.AgentId, options.SubjectType) is { } notWorkedOn)
        {
            return Refused(notWorkedOn, "The agent does not work on this kind of subject.");
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
            new NhAssistantConsultationTarget(thread.Data!, subject, access, runOptions, timeout, agent));
    }

    /// <summary>
    /// The consultation limit of the asking turn: its agent's own, otherwise the application's;
    /// <see langword="null"/> for no limit. A consultation outside a turn has no turn to count.
    /// </summary>
    private int? TurnLimitOf(NhAssistantTurnScope asking)
    {
        return registration.AgentRuns.TryGetValue(asking.Agent.Id, out var runOptions)
               && runOptions.MaxConsultationsPerTurn is { } own
            ? own
            : asking.Limits.MaxConsultationsPerTurn;
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
                ConsultationSource = consultation.ConsultationVersion is null
                    ? NhAssistantConsultationSources.Code
                    : NhAssistantConsultationSources.Data,
                ConsultationVersion = consultation.ConsultationVersion,
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
        // A proposal the asker decides in their own conversation is nothing for the approvers.
        var waiting = (await runs.GetConsultationApprovalsAsync(consultation.Id, CancellationToken.None))
            .Where(approval => approval.Status == NhAssistantApprovalStatuses.Pending
                && approval.Mode == NhAssistantApprovalModes.Deferred)
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

    private sealed record NhAssistantConsultationTarget(
        AssistantThread Thread,
        NhAiSubjectInfo Subject,
        NhAiSubjectAccess Access,
        NhAssistantAgentRunOptions RunOptions,
        TimeSpan Timeout,
        NhAssistantAgent Agent);
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

    /// <summary>
    /// One of <see cref="NhAssistantConsultationSources"/>.
    /// </summary>
    public string? ConsultationSource { get; init; }

    /// <summary>
    /// The version of a consultation defined as data; <see langword="null"/> for one in code.
    /// </summary>
    public int? ConsultationVersion { get; init; }
}
