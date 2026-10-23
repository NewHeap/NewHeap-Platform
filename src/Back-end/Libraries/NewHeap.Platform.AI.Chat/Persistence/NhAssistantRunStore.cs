using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NewHeap.Platform.AI.Chat.Entities;

namespace NewHeap.Platform.AI.Chat.Persistence;

/// <summary>
/// Persistence of agent threads, runs, queued inputs, subject member snapshots and the approval
/// queries of the inbox and the maintenance job. Every call uses its own short-lived context;
/// status transitions are compare-and-set updates, so concurrent workers never overwrite each other.
/// Whether a proposal is still open, and which decisions are recent, is read from the application's
/// clock (a registered <see cref="TimeProvider"/>, otherwise the system clock), which a proposal's
/// lifetime follows; threads, runs and inputs keep the system clock.
/// </summary>
internal sealed class NhAssistantRunStore(NhAssistantDbContextFactory contextFactory, TimeProvider? timeProvider = null)
{
    /// <summary>
    /// The failure code of a queued run whose trigger never reached the background-operation
    /// store, for example because the application's transaction rolled back.
    /// </summary>
    public const string OrphanedRunCode = "assistant-trigger-rolled-back";

    private static readonly string[] ActiveRunStatuses =
    [
        NhAssistantRunStatuses.Queued,
        NhAssistantRunStatuses.Running,
        NhAssistantRunStatuses.WaitingForApproval
    ];

    private static readonly string[] EndedRunStatuses =
    [
        NhAssistantRunStatuses.Completed,
        NhAssistantRunStatuses.Failed,
        NhAssistantRunStatuses.Cancelled
    ];

    private readonly TimeProvider _applicationClock = timeProvider ?? TimeProvider.System;

    /// <summary>
    /// The live thread of the agent about the subject, or with <paramref name="shadow"/> its shadow
    /// thread.
    /// </summary>
    public async Task<AssistantThread?> FindThreadAsync(
        string scopeKey,
        string agentId,
        NhAiSubjectRef subject,
        CancellationToken cancellationToken,
        bool shadow = false)
    {
        await using var context = contextFactory.CreateDbContext();
        return await context.Threads
            .AsNoTracking()
            .SingleOrDefaultAsync(
                thread => thread.ScopeKey == scopeKey
                    && thread.AgentId == agentId
                    && thread.SubjectType == subject.Type
                    && thread.SubjectId == subject.Id
                    && thread.Shadow == shadow,
                cancellationToken);
    }

    /// <summary>
    /// The subject types the agents have threads about, each pair once, ordered by agent and type;
    /// <paramref name="agentIds"/> limits the agents, <see langword="null"/> reads every agent. At
    /// most <paramref name="limit"/> pairs.
    /// </summary>
    public async Task<IReadOnlyList<(string AgentId, string SubjectType)>> ListThreadSubjectTypesAsync(
        IReadOnlyCollection<string>? agentIds,
        int limit,
        CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        var threads = context.Threads.AsNoTracking();
        if (agentIds is not null)
        {
            var ids = agentIds.Distinct(StringComparer.Ordinal).ToArray();
            threads = threads.Where(thread => ids.Contains(thread.AgentId));
        }

        var pairs = await threads
            .Select(thread => new { thread.AgentId, thread.SubjectType })
            .Distinct()
            .OrderBy(pair => pair.AgentId)
            .ThenBy(pair => pair.SubjectType)
            .Take(limit)
            .ToListAsync(cancellationToken);
        return pairs.Select(pair => (pair.AgentId, pair.SubjectType)).ToArray();
    }

    public async Task<AssistantThread?> FindThreadByConversationAsync(
        Guid conversationId,
        CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        return await context.Threads
            .AsNoTracking()
            .SingleOrDefaultAsync(thread => thread.ConversationId == conversationId, cancellationToken);
    }

    /// <summary>
    /// The threads among <paramref name="conversationIds"/>, by conversation id; unknown ids are left out.
    /// </summary>
    public async Task<IReadOnlyDictionary<Guid, AssistantThread>> FindThreadsByConversationsAsync(
        IReadOnlyCollection<Guid> conversationIds,
        CancellationToken cancellationToken)
    {
        if (conversationIds.Count == 0)
        {
            return new Dictionary<Guid, AssistantThread>();
        }

        var ids = conversationIds.Distinct().ToArray();
        await using var context = contextFactory.CreateDbContext();
        return await context.Threads
            .AsNoTracking()
            .Where(thread => ids.Contains(thread.ConversationId))
            .ToDictionaryAsync(thread => thread.ConversationId, cancellationToken);
    }

    /// <summary>
    /// Adds a thread and its conversation, or returns the thread another worker created first for
    /// the same scope key, agent and subject.
    /// </summary>
    public async Task<AssistantThread> AddThreadAsync(
        AssistantConversation conversation,
        AssistantThread thread,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        ArgumentNullException.ThrowIfNull(thread);
        try
        {
            await using var context = contextFactory.CreateDbContext();
            context.Conversations.Add(conversation);
            context.Threads.Add(thread);
            await context.SaveChangesAsync(cancellationToken);
            return thread;
        }
        catch (DbUpdateException)
        {
            // The unique thread key lost a race; the winner's thread is the thread.
            var existing = await FindThreadAsync(
                thread.ScopeKey,
                thread.AgentId,
                new NhAiSubjectRef(thread.SubjectType, thread.SubjectId),
                cancellationToken,
                thread.Shadow);
            return existing ?? throw new InvalidOperationException(
                "The agent thread could not be created and no existing thread was found.");
        }
    }

    public async Task UpdateThreadSubjectAsync(
        Guid conversationId,
        NhAiSubjectInfo subject,
        CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        await context.Threads
            .Where(thread => thread.ConversationId == conversationId)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(thread => thread.SubjectDisplayName, subject.DisplayName)
                    .SetProperty(thread => thread.SubjectVersion, subject.Version)
                    .SetProperty(thread => thread.DivisionId, subject.DivisionId)
                    .SetProperty(thread => thread.UpdatedAt, DateTimeOffset.UtcNow),
                cancellationToken);
    }

    public async Task<IReadOnlyList<AssistantThread>> FindThreadsForSubjectAsync(
        string scopeKey,
        NhAiSubjectRef subject,
        CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        return await context.Threads
            .AsNoTracking()
            .Where(thread => thread.ScopeKey == scopeKey
                && thread.SubjectType == subject.Type
                && thread.SubjectId == subject.Id)
            .OrderBy(thread => thread.AgentId)
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Replaces the member snapshot of one subject.
    /// </summary>
    public async Task ReplaceMembersAsync(
        string scopeKey,
        NhAiSubjectRef subject,
        IReadOnlyList<NhAiSubjectMember> members,
        DateTimeOffset snapshotAt,
        CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        await context.SubjectMembers
            .Where(member => member.ScopeKey == scopeKey
                && member.SubjectType == subject.Type
                && member.SubjectId == subject.Id)
            .ExecuteDeleteAsync(cancellationToken);
        foreach (var member in members
                     .Where(member => member.Role != NhAiSubjectRole.None)
                     .GroupBy(member => member.ActorId, StringComparer.Ordinal)
                     .Select(group => group.OrderByDescending(member => member.Role).First()))
        {
            context.SubjectMembers.Add(new AssistantSubjectMember
            {
                ScopeKey = scopeKey,
                SubjectType = subject.Type,
                SubjectId = subject.Id,
                ActorId = member.ActorId,
                Role = member.Role,
                NamedRolesJson = member.NamedRoles.Count == 0
                    ? null
                    : JsonSerializer.Serialize(member.NamedRoles.Order(StringComparer.Ordinal).ToArray()),
                DisplayName = member.DisplayName is { Length: > 200 } name ? name[..200] : member.DisplayName,
                SnapshotAt = snapshotAt
            });
        }

        await context.SaveChangesAsync(cancellationToken);
        await context.Threads
            .Where(thread => thread.ScopeKey == scopeKey
                && thread.SubjectType == subject.Type
                && thread.SubjectId == subject.Id)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(thread => thread.MembersRefreshedAt, snapshotAt),
                cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<AssistantSubjectMember>> GetMembersAsync(
        string scopeKey,
        NhAiSubjectRef subject,
        CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        return await context.SubjectMembers
            .AsNoTracking()
            .Where(member => member.ScopeKey == scopeKey
                && member.SubjectType == subject.Type
                && member.SubjectId == subject.Id)
            .OrderBy(member => member.ActorId)
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// The names the subject member snapshots know for these actors. Someone who left every subject
    /// keeps no name, so callers show the id instead.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, string>> GetDisplayNamesAsync(
        IEnumerable<string?> actorIds,
        CancellationToken cancellationToken)
    {
        var distinct = actorIds
            .Where(actorId => !string.IsNullOrWhiteSpace(actorId))
            .Select(actorId => actorId!)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (distinct.Count == 0)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        // A person has the same name on every subject; one per actor is enough.
        await using var context = contextFactory.CreateDbContext();
        var rows = await context.SubjectMembers
            .AsNoTracking()
            .Where(member => distinct.Contains(member.ActorId) && member.DisplayName != null)
            .GroupBy(member => member.ActorId)
            .Select(group => new { ActorId = group.Key, DisplayName = group.Max(member => member.DisplayName) })
            .ToListAsync(cancellationToken);
        return rows.ToDictionary(row => row.ActorId, row => row.DisplayName!, StringComparer.Ordinal);
    }

    public async Task AddRunAsync(AssistantRun run, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        await using var context = contextFactory.CreateDbContext();
        context.Runs.Add(run);
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task<AssistantRun?> FindRunAsync(Guid runId, CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        return await context.Runs.AsNoTracking().SingleOrDefaultAsync(run => run.Id == runId, cancellationToken);
    }

    /// <summary>
    /// The oldest queued run of the thread in <paramref name="skillId"/>; a run that the agent's
    /// rules or triage will route has none. With <paramref name="candidateTriggerId"/> only a run of
    /// that trigger's candidate version, which never joins other work; without it no such run.
    /// </summary>
    public async Task<AssistantRun?> FindQueuedRunAsync(
        Guid conversationId,
        CancellationToken cancellationToken,
        string? skillId = null,
        bool candidate = false,
        string? candidateTriggerId = null)
    {
        await using var context = contextFactory.CreateDbContext();
        var triggerCandidate = candidateTriggerId is not null;
        return await context.Runs
            .AsNoTracking()
            .Where(run => run.ConversationId == conversationId
                && run.Status == NhAssistantRunStatuses.Queued
                && run.SkillId == skillId
                && run.SkillCandidate == candidate
                && run.TriggerCandidate == triggerCandidate
                && (!triggerCandidate || run.TriggerId == candidateTriggerId))
            .OrderBy(run => run.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);
    }

    /// <summary>
    /// Records the skill a run works in and who chose it.
    /// </summary>
    public async Task SetRunSkillAsync(
        Guid runId,
        string skillId,
        string chosenBy,
        CancellationToken cancellationToken,
        int? skillVersion = null)
    {
        await using var context = contextFactory.CreateDbContext();
        await context.Runs
            .Where(run => run.Id == runId)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(run => run.SkillId, skillId)
                    .SetProperty(run => run.SkillChosenBy, chosenBy)
                    .SetProperty(run => run.SkillVersion, skillVersion),
                cancellationToken);
    }

    /// <summary>
    /// The tool calls and model tokens of one skill's runs in one scope since <paramref name="since"/>.
    /// </summary>
    public async Task<(int ToolCalls, long Tokens)> GetSkillUsageSinceAsync(
        string agentId,
        string skillId,
        string? scopeKey,
        DateTimeOffset since,
        CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        var runsOfSkill = context.Runs
            .Where(run => run.CreatedAt >= since && run.SkillId == skillId)
            .Join(
                context.Threads.Where(thread => thread.AgentId == agentId && (scopeKey == null || thread.ScopeKey == scopeKey)),
                run => run.ConversationId,
                thread => thread.ConversationId,
                (run, _) => run);
        var toolCalls = await runsOfSkill.SumAsync(run => run.ToolCalls, cancellationToken);
        var tokens = await runsOfSkill.SumAsync(run => run.InputTokens + run.OutputTokens, cancellationToken);
        return (toolCalls, tokens);
    }

    public async Task<AssistantRun?> FindRunByEventKeyAsync(
        Guid conversationId,
        string triggerId,
        string eventKey,
        CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        return await context.Runs
            .AsNoTracking()
            .Where(run => run.ConversationId == conversationId
                && run.TriggerId == triggerId
                && run.TriggerEventKey == eventKey
                && run.FailureCode != NhAssistantRunStore.OrphanedRunCode)
            .FirstOrDefaultAsync(cancellationToken);
    }

    /// <summary>
    /// The tool calls and model tokens of the agent's runs in one scope since <paramref name="since"/>.
    /// </summary>
    public async Task<(int ToolCalls, long Tokens)> GetAgentUsageSinceAsync(
        string agentId,
        string scopeKey,
        DateTimeOffset since,
        CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        var runsOfAgent = context.Runs
            .Where(run => run.CreatedAt >= since)
            .Join(
                context.Threads.Where(thread => thread.AgentId == agentId && thread.ScopeKey == scopeKey),
                run => run.ConversationId,
                thread => thread.ConversationId,
                (run, _) => run);
        var toolCalls = await runsOfAgent.SumAsync(run => run.ToolCalls, cancellationToken);
        var tokens = await runsOfAgent.SumAsync(run => run.InputTokens + run.OutputTokens, cancellationToken);

        // Consultations spend the same daily budget as the agent's own runs.
        var consultations = context.Consultations
            .Where(consultation => consultation.AgentId == agentId
                && consultation.ScopeKey == scopeKey
                && consultation.CreatedAt >= since);
        toolCalls += await consultations.SumAsync(consultation => consultation.ToolCalls, cancellationToken);
        tokens += await consultations.SumAsync(consultation => consultation.InputTokens + consultation.OutputTokens, cancellationToken);
        return (toolCalls, tokens);
    }

    public async Task AddConsultationAsync(AssistantConsultation consultation, CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        context.Consultations.Add(consultation);
        await context.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Ends a running consultation once and records its usage.
    /// </summary>
    public async Task<bool> EndConsultationAsync(
        Guid consultationId,
        string status,
        string? failureCode,
        int toolCalls,
        long inputTokens,
        long outputTokens,
        int proposals,
        CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        var now = DateTimeOffset.UtcNow;
        var updated = await context.Consultations
            .Where(consultation => consultation.Id == consultationId
                && consultation.Status == NhAssistantConsultationStatuses.Running)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(consultation => consultation.Status, status)
                    .SetProperty(consultation => consultation.FailureCode, failureCode)
                    .SetProperty(consultation => consultation.ToolCalls, toolCalls)
                    .SetProperty(consultation => consultation.InputTokens, inputTokens)
                    .SetProperty(consultation => consultation.OutputTokens, outputTokens)
                    .SetProperty(consultation => consultation.ProposalCount, proposals)
                    .SetProperty(consultation => consultation.EndedAt, now),
                cancellationToken);
        return updated == 1;
    }

    /// <summary>
    /// The consultations a person caused since <paramref name="since"/>, through any assistant.
    /// </summary>
    public async Task<int> CountConsultationsSinceAsync(
        string askerActorId,
        DateTimeOffset since,
        CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        return await context.Consultations
            .CountAsync(
                consultation => consultation.AskerActorId == askerActorId && consultation.CreatedAt >= since,
                cancellationToken);
    }

    /// <summary>
    /// One page of the consultations of a thread, newest first.
    /// </summary>
    public async Task<(IReadOnlyList<AssistantConsultation> Items, int Total)> ListConsultationsAsync(
        Guid conversationId,
        int page,
        int itemsPerPage,
        CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        var query = context.Consultations
            .AsNoTracking()
            .Where(consultation => consultation.ConversationId == conversationId);
        var total = await query.CountAsync(cancellationToken);
        var items = await query
            .OrderByDescending(consultation => consultation.CreatedAt)
            .ThenBy(consultation => consultation.Id)
            .Skip((page - 1) * itemsPerPage)
            .Take(itemsPerPage)
            .ToListAsync(cancellationToken);
        return (items, total);
    }

    /// <summary>
    /// The proposals one consultation left, oldest first.
    /// </summary>
    public async Task<IReadOnlyList<AssistantApproval>> GetConsultationApprovalsAsync(
        Guid consultationId,
        CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        return await context.Approvals
            .AsNoTracking()
            .Where(approval => approval.ConsultationId == consultationId)
            .OrderBy(approval => approval.CreatedAt)
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// How many proposals each consultation left per route, derived from their mode and grant source.
    /// </summary>
    public async Task<IReadOnlyDictionary<Guid, IReadOnlyDictionary<string, int>>> CountConsultationProposalsByRouteAsync(
        IReadOnlyCollection<Guid> consultationIds,
        CancellationToken cancellationToken)
    {
        if (consultationIds.Count == 0)
        {
            return new Dictionary<Guid, IReadOnlyDictionary<string, int>>();
        }

        var ids = consultationIds.Select(id => (Guid?)id).ToArray();
        await using var context = contextFactory.CreateDbContext();
        var rows = await context.Approvals
            .AsNoTracking()
            .Where(approval => ids.Contains(approval.ConsultationId))
            .GroupBy(approval => new { approval.ConsultationId, approval.Mode, approval.GrantSource })
            .Select(group => new { group.Key.ConsultationId, group.Key.Mode, group.Key.GrantSource, Count = group.Count() })
            .ToListAsync(cancellationToken);
        return rows
            .GroupBy(row => row.ConsultationId!.Value)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyDictionary<string, int>)group
                    .GroupBy(row => NhAssistantConsultationRoutes.Of(row.ConsultationId, row.Mode, row.GrantSource)!, StringComparer.Ordinal)
                    .ToDictionary(route => route.Key, route => route.Sum(row => row.Count), StringComparer.Ordinal));
    }

    public async Task<AssistantConsultation?> FindConsultationAsync(Guid consultationId, CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        return await context.Consultations
            .AsNoTracking()
            .SingleOrDefaultAsync(consultation => consultation.Id == consultationId, cancellationToken);
    }

    /// <summary>
    /// How many proposals of consultations that execute with the agent's own rights wait in the thread
    /// for a decision and were caused by <paramref name="askerActorId"/> (CP15).
    /// </summary>
    public async Task<int> CountOpenAgentRightsConsultationProposalsAsync(
        Guid conversationId,
        string askerActorId,
        CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        return await context.Approvals
            .CountAsync(
                approval => approval.ConversationId == conversationId
                    && approval.ConsultationId != null
                    && approval.InitiatorActorId == askerActorId
                    && approval.Status == NhAssistantApprovalStatuses.Pending
                    && approval.Mode == NhAssistantApprovalModes.Deferred
                    && (approval.GrantSource == NhAiGrantSource.Agent || approval.GrantSource == NhAiGrantSource.Accountable),
                cancellationToken);
    }

    /// <summary>
    /// The pending proposal of a consultation that the person of the asking turn decides in the asking
    /// conversation, or <see langword="null"/>.
    /// </summary>
    public async Task<AssistantApproval?> FindPendingAskerDecisionAsync(
        Guid askingConversationId,
        Guid askingTurnId,
        CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        return await AskerDecisions(context, askingConversationId)
            .Where(row => row.Consultation.AskingTurnId == askingTurnId
                && row.Approval.Status == NhAssistantApprovalStatuses.Pending)
            .Select(row => row.Approval)
            .OrderByDescending(approval => approval.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);
    }

    /// <summary>
    /// The proposals of consultations that the asking conversation's people decide there, among
    /// <paramref name="approvalIds"/>, with their consultation and the consulted agent's thread.
    /// </summary>
    public async Task<IReadOnlyList<NhAssistantAskerDecisionRow>> GetAskerDecisionsAsync(
        Guid askingConversationId,
        IReadOnlyCollection<Guid> approvalIds,
        CancellationToken cancellationToken)
    {
        if (approvalIds.Count == 0)
        {
            return [];
        }

        var ids = approvalIds.ToArray();
        await using var context = contextFactory.CreateDbContext();
        var rows = await AskerDecisions(context, askingConversationId)
            .Where(row => ids.Contains(row.Approval.Id))
            .Join(
                context.Threads,
                row => row.Approval.ConversationId,
                thread => thread.ConversationId,
                (row, thread) => new { row.Approval, row.Consultation, Thread = thread })
            .ToListAsync(cancellationToken);
        return rows.Select(row => new NhAssistantAskerDecisionRow(row.Approval, row.Consultation, row.Thread)).ToArray();
    }

    /// <summary>
    /// The tool call of the asking conversation that waits for, or waited for, the asker's decision of
    /// a consultation proposal.
    /// </summary>
    public async Task<AssistantToolInvocation?> FindAskingCallAsync(
        Guid askingConversationId,
        Guid approvalId,
        CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        return await context.ToolInvocations
            .AsNoTracking()
            .Where(invocation => invocation.ConversationId == askingConversationId && invocation.ApprovalId == approvalId)
            .OrderByDescending(invocation => invocation.StartedAt)
            .FirstOrDefaultAsync(cancellationToken);
    }

    /// <summary>
    /// Ends the asking conversation's call that waited for the asker's decision of a consultation
    /// proposal, when it still waits, with the outcome as its result code.
    /// </summary>
    public async Task<bool> EndAskingCallAsync(
        Guid askingConversationId,
        Guid approvalId,
        string callStatus,
        string resultCode,
        CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        var now = DateTimeOffset.UtcNow;
        return await context.ToolInvocations
            .Where(invocation => invocation.ConversationId == askingConversationId
                && invocation.ApprovalId == approvalId
                && invocation.Status == NhAssistantToolCallStatuses.AwaitingApproval)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(invocation => invocation.Status, callStatus)
                    .SetProperty(invocation => invocation.ResultCode, resultCode)
                    .SetProperty(invocation => invocation.CompletedAt, now),
                cancellationToken) > 0;
    }

    /// <summary>
    /// The proposals the people of one asking conversation decide there: consultation proposals with
    /// mode inline whose consultation names the conversation.
    /// </summary>
    private static IQueryable<NhAssistantAskerDecisionQueryRow> AskerDecisions(NhAssistantDbContext context, Guid askingConversationId)
    {
        return context.Approvals
            .AsNoTracking()
            .Where(approval => approval.ConsultationId != null && approval.Mode == NhAssistantApprovalModes.Inline)
            .Join(
                context.Consultations.Where(consultation => consultation.AskingConversationId == askingConversationId),
                approval => approval.ConsultationId,
                consultation => (Guid?)consultation.Id,
                (approval, consultation) => new NhAssistantAskerDecisionQueryRow { Approval = approval, Consultation = consultation });
    }

    public async Task<int> CountRunningRunsAsync(string agentId, Guid excludedRunId, CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        return await context.Runs
            .Where(run => run.Status == NhAssistantRunStatuses.Running && run.Id != excludedRunId)
            .Join(
                context.Threads.Where(thread => thread.AgentId == agentId),
                run => run.ConversationId,
                thread => thread.ConversationId,
                (run, _) => run.Id)
            .CountAsync(cancellationToken);
    }

    /// <summary>
    /// Queued runs older than <paramref name="createdBefore"/>, for the maintenance job to find
    /// runs whose trigger transaction rolled back.
    /// </summary>
    public async Task<IReadOnlyList<AssistantRun>> GetStaleQueuedRunsAsync(
        DateTimeOffset createdBefore,
        int maximum,
        CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        return await context.Runs
            .AsNoTracking()
            .Where(run => run.Status == NhAssistantRunStatuses.Queued && run.CreatedAt < createdBefore)
            .OrderBy(run => run.CreatedAt)
            .Take(maximum)
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Runs that have not ended and have a background operation, in the order of
    /// <c>(CreatedAt, Id)</c>, after the run <paramref name="after"/>: one page of the maintenance
    /// job's check of runs against their operations. The job continues each round after the last run
    /// of the page it checked, so every run is checked in turn however many runs wait, also when
    /// several were created at the same time at a page boundary. The page is read through the
    /// <c>(Status, CreatedAt)</c> index: the condition keeps a range on <c>CreatedAt</c>, and the id
    /// only decides between runs created at the cursor's time.
    /// </summary>
    public async Task<IReadOnlyList<AssistantRun>> GetActiveRunsWithOperationAsync(
        (DateTimeOffset CreatedAt, Guid Id)? after,
        int maximum,
        CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        var query = context.Runs
            .AsNoTracking()
            .Where(run => ActiveRunStatuses.Contains(run.Status) && run.BackgroundOperationId != null);
        if (after is { } cursor)
        {
            var createdAt = cursor.CreatedAt;
            var id = cursor.Id;
            query = query.Where(run => run.CreatedAt >= createdAt && (run.CreatedAt > createdAt || run.Id > id));
        }

        return await query
            .OrderBy(run => run.CreatedAt)
            .ThenBy(run => run.Id)
            .Take(maximum)
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Runs that ended while an inline proposal of theirs was approved and not executed, at most
    /// <paramref name="maximum"/>: one page of the maintenance job's check whether their operation can
    /// still execute it. A person's cancel, the kill switch and subject retention leave such a
    /// proposal approved. The page is read from the approved proposals through the
    /// <c>(Status, ExpiresAt)</c> index, the earliest expiry first, and joins their runs by key, so it
    /// never reads the history of ended runs. Such proposals are few and each one leaves the page
    /// once its run's operation has ended, so no cursor is kept.
    /// </summary>
    public async Task<IReadOnlyList<AssistantRun>> GetEndedRunsWithApprovedInlineProposalsAsync(
        int maximum,
        CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        var ended = await context.Approvals
            .AsNoTracking()
            .Where(approval => approval.Status == NhAssistantApprovalStatuses.Approved
                && approval.Mode == NhAssistantApprovalModes.Inline
                && approval.RunId != null)
            .Join(
                context.Runs.AsNoTracking().Where(run => EndedRunStatuses.Contains(run.Status)),
                approval => approval.RunId,
                run => (Guid?)run.Id,
                (approval, run) => new { approval.ExpiresAt, ApprovalId = approval.Id, Run = run })
            .OrderBy(item => item.ExpiresAt)
            .ThenBy(item => item.ApprovalId)
            .Take(maximum)
            .Select(item => item.Run)
            .ToListAsync(cancellationToken);
        return ended.DistinctBy(run => run.Id).ToArray();
    }

    /// <summary>
    /// Releases a thread whose turn <paramref name="turnId"/> waits for an inline approval that will
    /// never be decided, because the run that waited in it ended without resuming it. A thread that
    /// another turn holds, or that no longer waits, stays as it is.
    /// </summary>
    public async Task<bool> TryReleaseWaitingTurnAsync(
        Guid conversationId,
        Guid turnId,
        CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        var now = DateTimeOffset.UtcNow;
        var affected = await context.Conversations
            .Where(conversation => conversation.Id == conversationId
                && conversation.Status == NhAssistantConversationStatuses.WaitingForApproval
                && conversation.ActiveTurnId == turnId)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(conversation => conversation.Status, NhAssistantConversationStatuses.Idle)
                    .SetProperty(conversation => conversation.ActiveTurnId, (Guid?)null)
                    .SetProperty(conversation => conversation.ActiveActorId, (string?)null)
                    .SetProperty(conversation => conversation.UpdatedAt, now)
                    .SetProperty(conversation => conversation.ConcurrencyStamp, Guid.NewGuid()),
                cancellationToken);
        return affected == 1;
    }

    /// <summary>
    /// Whether the thread still waits in turn <paramref name="turnId"/> for an inline decision. It no
    /// longer does once the run that waited in it ended without resuming it and released the turn.
    /// </summary>
    public async Task<bool> WaitsInTurnAsync(Guid conversationId, Guid turnId, CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        return await context.Conversations
            .AsNoTracking()
            .AnyAsync(
                conversation => conversation.Id == conversationId
                    && conversation.Status == NhAssistantConversationStatuses.WaitingForApproval
                    && conversation.ActiveTurnId == turnId,
                cancellationToken);
    }

    /// <summary>
    /// Approved deferred proposals without an execution operation that were decided before
    /// <paramref name="decidedBefore"/>, of run threads and consultations alike, oldest decision
    /// first. Proposals of stopped agents and of <paramref name="heldAgentIds"/> stay out of the
    /// query, so the proposals they hold never hide the ones behind them.
    /// </summary>
    public async Task<IReadOnlyList<AssistantApproval>> GetHeldExecutionsAsync(
        DateTimeOffset decidedBefore,
        IReadOnlyCollection<string> heldAgentIds,
        int maximum,
        CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        var held = heldAgentIds.ToArray();
        return await context.Approvals
            .AsNoTracking()
            .Where(approval => approval.Status == NhAssistantApprovalStatuses.Approved
                && approval.Mode == NhAssistantApprovalModes.Deferred
                && approval.ExecutionOperationId == null
                && approval.DecidedAt != null
                && approval.DecidedAt < decidedBefore)
            .Where(approval => context.Threads.Any(thread => thread.ConversationId == approval.ConversationId
                && !held.Contains(thread.AgentId)
                && context.Agents.Any(agent => agent.Id == thread.AgentId
                    && agent.IsEnabled
                    && agent.RunState != NhAssistantAgentRunStates.Stopped)))
            .OrderBy(approval => approval.DecidedAt)
            .ThenBy(approval => approval.Id)
            .Take(maximum)
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// The approved deferred proposals without an execution operation in the threads of one agent,
    /// oldest decision first: the executions a stopped agent held.
    /// </summary>
    public async Task<IReadOnlyList<(AssistantApproval Approval, AssistantThread Thread)>> GetAgentHeldExecutionsAsync(
        string agentId,
        int maximum,
        CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        var held = await context.Approvals
            .AsNoTracking()
            .Where(approval => approval.Status == NhAssistantApprovalStatuses.Approved
                && approval.Mode == NhAssistantApprovalModes.Deferred
                && approval.ExecutionOperationId == null)
            .Join(
                context.Threads.AsNoTracking().Where(thread => thread.AgentId == agentId),
                approval => approval.ConversationId,
                thread => thread.ConversationId,
                (approval, thread) => new { Approval = approval, Thread = thread })
            .OrderBy(item => item.Approval.DecidedAt)
            .ThenBy(item => item.Approval.Id)
            .Take(maximum)
            .ToListAsync(cancellationToken);
        return held.Select(item => (item.Approval, item.Thread)).ToArray();
    }

    public async Task<IReadOnlyList<AssistantApproval>> GetApprovalsInStatusAsync(
        string status,
        int maximum,
        CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        return await context.Approvals
            .AsNoTracking()
            .Where(approval => approval.Status == status && approval.RunId != null)
            .OrderBy(approval => approval.CreatedAt)
            .Take(maximum)
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// True when <paramref name="actorId"/> decided an inline proposal of the run that the
    /// background operation executes within the last few minutes. Only that person may wake the run.
    /// </summary>
    public async Task<bool> HasRecentInlineDecisionAsync(
        Guid operationId,
        string actorId,
        CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        // Decisions are recorded on the application's clock.
        var since = _applicationClock.GetUtcNow().AddMinutes(-5);
        return await context.Runs
            .Where(run => run.BackgroundOperationId == operationId)
            .Join(
                context.Approvals.Where(approval => approval.DecidedByActorId == actorId
                    && approval.DecidedAt >= since
                    && approval.Mode == NhAssistantApprovalModes.Inline),
                run => (Guid?)run.Id,
                approval => approval.RunId,
                (run, approval) => approval.Id)
            .AnyAsync(cancellationToken);
    }

    /// <summary>
    /// The agent thread a background operation works in: the thread of the run it executes, or of the
    /// proposal whose execution it is; <see langword="null"/> for another operation.
    /// </summary>
    public async Task<AssistantThread?> FindThreadOfOperationAsync(Guid operationId, CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        var ofRun = await (
                from run in context.Runs.AsNoTracking()
                join thread in context.Threads.AsNoTracking() on run.ConversationId equals thread.ConversationId
                where run.BackgroundOperationId == operationId
                select thread)
            .FirstOrDefaultAsync(cancellationToken);
        if (ofRun is not null)
        {
            return ofRun;
        }

        return await (
                from approval in context.Approvals.AsNoTracking()
                join thread in context.Threads.AsNoTracking() on approval.ConversationId equals thread.ConversationId
                where approval.ExecutionOperationId == operationId
                select thread)
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<AssistantRun?> FindActiveRunAsync(Guid conversationId, CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        return await context.Runs
            .AsNoTracking()
            .Where(run => run.ConversationId == conversationId && ActiveRunStatuses.Contains(run.Status))
            .OrderBy(run => run.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);
    }

    /// <summary>
    /// The oldest active run whose work a trigger's conflict behavior concerns: with
    /// <paramref name="candidateTriggerId"/> a run of that trigger's candidate version, otherwise any
    /// run but a trigger candidate's, so a candidate neither waits for, ignores, nor supersedes the
    /// live version's work, and the live version never supersedes the candidate's.
    /// </summary>
    public async Task<AssistantRun?> FindActiveRunForConflictAsync(
        Guid conversationId,
        string? candidateTriggerId,
        CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        var triggerCandidate = candidateTriggerId is not null;
        return await context.Runs
            .AsNoTracking()
            .Where(run => run.ConversationId == conversationId
                && ActiveRunStatuses.Contains(run.Status)
                && run.TriggerCandidate == triggerCandidate
                && (!triggerCandidate || run.TriggerId == candidateTriggerId))
            .OrderBy(run => run.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<(IReadOnlyList<AssistantRun> Items, int Total)> ListRunsAsync(
        Guid conversationId,
        int page,
        int itemsPerPage,
        CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        var query = context.Runs.AsNoTracking().Where(run => run.ConversationId == conversationId);
        var total = await query.CountAsync(cancellationToken);
        var items = await query
            .OrderByDescending(run => run.CreatedAt)
            .ThenBy(run => run.Id)
            .Skip((page - 1) * itemsPerPage)
            .Take(itemsPerPage)
            .ToListAsync(cancellationToken);
        return (items, total);
    }

    /// <summary>
    /// The runs of the thread since <paramref name="since"/>. With <paramref name="candidateTriggerId"/>
    /// only the runs of that trigger's candidate version, otherwise every run but a trigger
    /// candidate's: a candidate counts against its own limit and never uses up the live version's.
    /// </summary>
    public async Task<int> CountRunsSinceAsync(
        Guid conversationId,
        DateTimeOffset since,
        CancellationToken cancellationToken,
        string? candidateTriggerId = null)
    {
        await using var context = contextFactory.CreateDbContext();
        var triggerCandidate = candidateTriggerId is not null;
        return await context.Runs.CountAsync(
            run => run.ConversationId == conversationId
                && run.CreatedAt >= since
                && run.TriggerCandidate == triggerCandidate
                && (!triggerCandidate || run.TriggerId == candidateTriggerId),
            cancellationToken);
    }

    public async Task<int> CountActiveRunsAsync(string agentId, CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        return await context.Runs
            .Where(run => ActiveRunStatuses.Contains(run.Status))
            .Join(
                context.Threads.Where(thread => thread.AgentId == agentId),
                run => run.ConversationId,
                thread => thread.ConversationId,
                (run, _) => run.Id)
            .CountAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<AssistantRun>> GetActiveRunsAsync(
        string agentId,
        CancellationToken cancellationToken,
        string? skillId = null)
    {
        await using var context = contextFactory.CreateDbContext();
        return await context.Runs
            .AsNoTracking()
            .Where(run => ActiveRunStatuses.Contains(run.Status) && (skillId == null || run.SkillId == skillId))
            .Join(
                context.Threads.Where(thread => thread.AgentId == agentId),
                run => run.ConversationId,
                thread => thread.ConversationId,
                (run, _) => run)
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Moves a run to <paramref name="status"/> when it is in one of <paramref name="expected"/>.
    /// </summary>
    public async Task<bool> TrySetRunStatusAsync(
        Guid runId,
        IReadOnlyCollection<string> expected,
        string status,
        CancellationToken cancellationToken,
        string? failureClass = null,
        string? failureCode = null,
        Guid? turnId = null)
    {
        await using var context = contextFactory.CreateDbContext();
        var now = DateTimeOffset.UtcNow;
        var terminal = NhAssistantRunStatuses.IsTerminal(status);
        var updated = await context.Runs
            .Where(run => run.Id == runId && expected.Contains(run.Status))
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(run => run.Status, status)
                    .SetProperty(run => run.FailureClass, failureClass)
                    .SetProperty(run => run.FailureCode, failureCode)
                    .SetProperty(run => run.TurnId, run => turnId ?? run.TurnId)
                    .SetProperty(run => run.StartedAt, run => run.StartedAt ?? now)
                    .SetProperty(run => run.EndedAt, run => terminal ? now : run.EndedAt)
                    .SetProperty(run => run.ConcurrencyStamp, Guid.NewGuid()),
                cancellationToken);
        return updated == 1;
    }

    /// <summary>
    /// Adds what a turn of the run used. Its model calls are not among them: the assistant's budget
    /// manager counts each on the run when its budget is reserved.
    /// </summary>
    public async Task RecordRunUsageAsync(
        Guid runId,
        int toolCalls,
        long inputTokens,
        long outputTokens,
        int proposals,
        CancellationToken cancellationToken,
        string? kind = null,
        bool usedRestrictedResults = false)
    {
        await using var context = contextFactory.CreateDbContext();
        await context.Runs
            .Where(run => run.Id == runId)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(run => run.ToolCalls, run => run.ToolCalls + toolCalls)
                    .SetProperty(run => run.InputTokens, run => run.InputTokens + inputTokens)
                    .SetProperty(run => run.OutputTokens, run => run.OutputTokens + outputTokens)
                    .SetProperty(run => run.ProposalCount, run => run.ProposalCount + proposals)
                    .SetProperty(run => run.Kind, run => kind ?? run.Kind),
                cancellationToken);
        if (usedRestrictedResults)
        {
            // Once set, a run keeps it: any of its turns may have read restricted results.
            await context.Runs
                .Where(run => run.Id == runId)
                .ExecuteUpdateAsync(setters => setters.SetProperty(run => run.UsedRestrictedResults, true), cancellationToken);
        }
    }

    /// <summary>
    /// Adds the personal MCP servers a turn left out to the run: one entry per server with its latest
    /// skip code. The value is written only while it is the one read, so no entry of another turn is
    /// lost.
    /// </summary>
    /// <exception cref="InvalidOperationException">The value kept changing; only the run's own operation writes it.</exception>
    public async Task AddMcpConnectionSkipsAsync(
        Guid runId,
        IReadOnlyList<NewHeap.Platform.AI.Chat.Runs.NhAssistantRunMcpSkip> skips,
        CancellationToken cancellationToken)
    {
        if (skips.Count == 0)
        {
            return;
        }

        await using var context = contextFactory.CreateDbContext();
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var current = await context.Runs
                .AsNoTracking()
                .Where(run => run.Id == runId)
                .Select(run => new { run.McpConnectionSkips })
                .SingleOrDefaultAsync(cancellationToken);
            if (current is null)
            {
                return;
            }

            var previous = current.McpConnectionSkips;
            var merged = NewHeap.Platform.AI.Chat.Runs.NhAssistantRunMcpSkips.Merge(previous, skips);
            var updated = await context.Runs
                .Where(run => run.Id == runId && run.McpConnectionSkips == previous)
                .ExecuteUpdateAsync(setters => setters.SetProperty(run => run.McpConnectionSkips, merged), cancellationToken);
            if (updated == 1)
            {
                return;
            }
        }

        throw new InvalidOperationException($"The MCP skips of run {runId} changed while they were written.");
    }

    public async Task SetRunAttemptAsync(
        Guid runId,
        int attempt,
        Guid? backgroundOperationId,
        CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        await context.Runs
            .Where(run => run.Id == runId)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(run => run.Attempt, attempt)
                    .SetProperty(run => run.BackgroundOperationId, run => backgroundOperationId ?? run.BackgroundOperationId),
                cancellationToken);
    }

    /// <summary>
    /// Every agent thread about the subject, in every scope and of every agent.
    /// </summary>
    public async Task<IReadOnlyList<AssistantThread>> FindThreadsBySubjectAsync(NhAiSubjectRef subject, CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        return await context.Threads
            .AsNoTracking()
            .Where(thread => thread.SubjectType == subject.Type && thread.SubjectId == subject.Id)
            .OrderBy(thread => thread.ConversationId)
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// The runs of a thread that have not ended.
    /// </summary>
    public async Task<IReadOnlyList<AssistantRun>> GetOpenRunsAsync(Guid conversationId, CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        return await context.Runs
            .AsNoTracking()
            .Where(run => run.ConversationId == conversationId
                && (run.Status == NhAssistantRunStatuses.Queued
                    || run.Status == NhAssistantRunStatuses.Running
                    || run.Status == NhAssistantRunStatuses.WaitingForApproval))
            .OrderBy(run => run.CreatedAt)
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Deletes a thread with its conversation; messages, tool calls, proposals, runs and inputs
    /// follow by cascade. The member snapshot of the subject is deleted with the last thread.
    /// </summary>
    public async Task DeleteThreadAsync(AssistantThread thread, CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        await context.Conversations
            .Where(conversation => conversation.Id == thread.ConversationId)
            .ExecuteDeleteAsync(cancellationToken);
        var otherThreads = await context.Threads
            .AnyAsync(
                other => other.ScopeKey == thread.ScopeKey
                    && other.SubjectType == thread.SubjectType
                    && other.SubjectId == thread.SubjectId
                    && other.ConversationId != thread.ConversationId,
                cancellationToken);
        if (!otherThreads)
        {
            await context.SubjectMembers
                .Where(member => member.ScopeKey == thread.ScopeKey
                    && member.SubjectType == thread.SubjectType
                    && member.SubjectId == thread.SubjectId)
                .ExecuteDeleteAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>
    /// Replaces the working memory of a thread when nobody changed it since it was read.
    /// </summary>
    public async Task<bool> TryUpdateMemoryAsync(
        Guid conversationId,
        Guid expectedStamp,
        string? memoryJson,
        int schemaVersion,
        CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        var now = DateTimeOffset.UtcNow;
        var updated = await context.Threads
            .Where(thread => thread.ConversationId == conversationId && thread.MemoryConcurrencyStamp == expectedStamp)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(thread => thread.MemoryJson, memoryJson)
                    .SetProperty(thread => thread.MemorySchemaVersion, schemaVersion)
                    .SetProperty(thread => thread.MemoryConcurrencyStamp, Guid.NewGuid())
                    .SetProperty(thread => thread.UpdatedAt, now),
                cancellationToken);
        return updated == 1;
    }

    public async Task SetRunSummaryAsync(Guid runId, string summary, CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        var bounded = summary.Length > 4_000 ? summary[..4_000] : summary;
        await context.Runs
            .Where(run => run.Id == runId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(run => run.Summary, bounded), cancellationToken);
    }

    /// <summary>
    /// The stored parts of the assistant messages of one turn, oldest first: what the turn answered.
    /// </summary>
    public async Task<IReadOnlyList<string>> GetTurnAnswerPartsAsync(
        Guid conversationId,
        Guid turnId,
        CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        return await context.Messages
            .AsNoTracking()
            .Where(message => message.ConversationId == conversationId
                && message.TurnId == turnId
                && message.Role == NhAssistantMessageRoles.Assistant)
            .OrderBy(message => message.Sequence)
            .Select(message => message.PartsJson)
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Stores the run's result, already bounded to <see cref="NhAssistantLimits.MaxRunResultCharacters"/>.
    /// </summary>
    public async Task SetRunResultAsync(
        Guid runId,
        string? result,
        bool truncated,
        string? resultsJson,
        CancellationToken cancellationToken)
    {
        if (result is { Length: > NhAssistantLimits.MaxRunResultCharacters })
        {
            throw new ArgumentOutOfRangeException(nameof(result), "A run result is bounded before it is stored.");
        }
        if (resultsJson is { Length: > Runs.NhAssistantRunResults.MaxLocalizedSerializedLength })
        {
            throw new ArgumentOutOfRangeException(nameof(resultsJson), "The results per culture are bounded before they are stored.");
        }

        await using var context = contextFactory.CreateDbContext();
        await context.Runs
            .Where(run => run.Id == runId)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(run => run.Result, result)
                    .SetProperty(run => run.ResultTruncated, truncated)
                    .SetProperty(run => run.ResultsJson, resultsJson),
                cancellationToken);
    }

    /// <summary>
    /// The run with its thread, or <see langword="null"/> for an unknown run.
    /// </summary>
    public async Task<NhAssistantRunRow?> FindRunWithThreadAsync(Guid runId, CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        var row = await (
                from run in context.Runs.AsNoTracking()
                join thread in context.Threads.AsNoTracking() on run.ConversationId equals thread.ConversationId
                where run.Id == runId
                select new { run, thread })
            .SingleOrDefaultAsync(cancellationToken);
        return row is null ? null : new NhAssistantRunRow(row.run, row.thread);
    }

    /// <summary>
    /// One page of the runs of an agent that match the filter, live and shadow, newest first, with
    /// their threads and the number of matching runs.
    /// </summary>
    public async Task<(IReadOnlyList<NhAssistantRunRow> Items, int Total)> ListAgentRunsAsync(
        string agentId,
        NhAssistantRunFilter filter,
        int page,
        int itemsPerPage,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ArgumentNullException.ThrowIfNull(filter);
        await using var context = contextFactory.CreateDbContext();
        var threads = FilterThreads(context, filter with { AgentId = agentId });
        var query = FilterRuns(context, filter)
            .Join(threads, run => run.ConversationId, thread => thread.ConversationId, (run, thread) => new { run, thread });
        var total = await query.CountAsync(cancellationToken);
        var rows = await query
            .OrderByDescending(row => row.run.CreatedAt)
            .ThenByDescending(row => row.run.Id)
            .Skip((page - 1) * itemsPerPage)
            .Take(itemsPerPage)
            .ToListAsync(cancellationToken);
        return (rows.Select(row => new NhAssistantRunRow(row.run, row.thread)).ToArray(), total);
    }

    /// <summary>
    /// The threads with at least one run that matches the filter, in <paramref name="tenantId"/>'s
    /// tenant (none for a caller without one), ordered by their newest matching run, at most
    /// <paramref name="maximum"/>. Shadow threads only with <paramref name="includeShadow"/>. With
    /// <paramref name="includeWholeApplication"/> the whole application's threads, stored without a
    /// tenant, count as well, for a caller of the single tenant.
    /// </summary>
    public async Task<IReadOnlyList<AssistantThread>> FindThreadsWithRunsAsync(
        NhAssistantRunFilter filter,
        string? tenantId,
        bool includeShadow,
        int maximum,
        CancellationToken cancellationToken,
        bool includeWholeApplication = false)
    {
        ArgumentNullException.ThrowIfNull(filter);
        await using var context = contextFactory.CreateDbContext();
        var threads = includeWholeApplication
            ? FilterThreads(context, filter).Where(thread => thread.TenantId == tenantId
                || (thread.TenantId == null && thread.SubjectType == NhAiSubjectRef.SystemType))
            : FilterThreads(context, filter).Where(thread => thread.TenantId == tenantId);
        if (!includeShadow)
        {
            threads = threads.Where(thread => !thread.Shadow);
        }

        var newest = await FilterRuns(context, filter)
            .Join(threads, run => run.ConversationId, thread => thread.ConversationId, (run, thread) => run)
            .GroupBy(run => run.ConversationId)
            .Select(group => new { ConversationId = group.Key, Latest = group.Max(run => run.CreatedAt) })
            .OrderByDescending(item => item.Latest)
            .ThenBy(item => item.ConversationId)
            .Take(maximum)
            .ToListAsync(cancellationToken);
        if (newest.Count == 0)
        {
            return [];
        }

        var ids = newest.Select(item => item.ConversationId).ToArray();
        var found = await context.Threads
            .AsNoTracking()
            .Where(thread => ids.Contains(thread.ConversationId))
            .ToDictionaryAsync(thread => thread.ConversationId, cancellationToken);
        return ids.Where(found.ContainsKey).Select(id => found[id]).ToArray();
    }

    /// <summary>
    /// One page of the runs in <paramref name="conversationIds"/> that match the filter's status and
    /// period, newest first, with their threads and the number of matching runs.
    /// </summary>
    public async Task<(IReadOnlyList<NhAssistantRunRow> Items, int Total)> ListRunsOfThreadsAsync(
        IReadOnlyCollection<Guid> conversationIds,
        NhAssistantRunFilter filter,
        int page,
        int itemsPerPage,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(conversationIds);
        ArgumentNullException.ThrowIfNull(filter);
        if (conversationIds.Count == 0)
        {
            return ([], 0);
        }

        var ids = conversationIds.Distinct().ToArray();
        await using var context = contextFactory.CreateDbContext();
        var query = FilterRuns(context, filter)
            .Where(run => ids.Contains(run.ConversationId))
            .Join(context.Threads.AsNoTracking(), run => run.ConversationId, thread => thread.ConversationId, (run, thread) => new { run, thread });
        var total = await query.CountAsync(cancellationToken);
        var rows = await query
            .OrderByDescending(row => row.run.CreatedAt)
            .ThenByDescending(row => row.run.Id)
            .Skip((page - 1) * itemsPerPage)
            .Take(itemsPerPage)
            .ToListAsync(cancellationToken);
        return (rows.Select(row => new NhAssistantRunRow(row.run, row.thread)).ToArray(), total);
    }

    /// <summary>
    /// The runs that match the filter's status and period. Times are compared in UTC.
    /// </summary>
    private static IQueryable<AssistantRun> FilterRuns(NhAssistantDbContext context, NhAssistantRunFilter filter)
    {
        var runs = context.Runs.AsNoTracking();
        if (filter.Status is { } status)
        {
            runs = runs.Where(run => run.Status == status);
        }
        if (filter.From is { } from)
        {
            var since = from.ToUniversalTime();
            runs = runs.Where(run => run.CreatedAt >= since);
        }
        if (filter.To is { } to)
        {
            var until = to.ToUniversalTime();
            runs = runs.Where(run => run.CreatedAt < until);
        }

        return runs;
    }

    /// <summary>
    /// The threads that match the filter's agent and subject.
    /// </summary>
    private static IQueryable<AssistantThread> FilterThreads(NhAssistantDbContext context, NhAssistantRunFilter filter)
    {
        var threads = context.Threads.AsNoTracking();
        if (filter.AgentId is { } agentId)
        {
            threads = threads.Where(thread => thread.AgentId == agentId);
        }
        if (filter.SubjectType is { } subjectType)
        {
            threads = threads.Where(thread => thread.SubjectType == subjectType);
        }
        if (filter.SubjectId is { } subjectId)
        {
            threads = threads.Where(thread => thread.SubjectId == subjectId);
        }

        return threads;
    }

    public async Task MarkRunCancelRequestedAsync(Guid runId, string actorId, CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        await context.Runs
            .Where(run => run.Id == runId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(run => run.CancelledByActorId, actorId), cancellationToken);
    }

    public async Task<IReadOnlyList<string>> GetRecentSummariesAsync(
        Guid conversationId,
        Guid excludedRunId,
        int maximum,
        CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        var summaries = await context.Runs
            .AsNoTracking()
            .Where(run => run.ConversationId == conversationId
                && run.Id != excludedRunId
                && run.Summary != null)
            .OrderByDescending(run => run.CreatedAt)
            .Take(maximum)
            .Select(run => run.Summary!)
            .ToListAsync(cancellationToken);
        summaries.Reverse();
        return summaries;
    }

    public async Task AddQueuedInputAsync(AssistantQueuedInput input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        await using var context = contextFactory.CreateDbContext();
        context.QueuedInputs.Add(input);
        await context.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Assigns every waiting input of the thread to the run and returns the inputs the run owns,
    /// oldest first. Calling it again for the same run returns the same inputs plus any that
    /// arrived since.
    /// </summary>
    public async Task<IReadOnlyList<AssistantQueuedInput>> ClaimQueuedInputsAsync(
        Guid conversationId,
        Guid runId,
        CancellationToken cancellationToken,
        string? skillId = null,
        bool candidate = false,
        string? candidateTriggerId = null)
    {
        await using var context = contextFactory.CreateDbContext();
        var now = DateTimeOffset.UtcNow;
        var triggerCandidate = candidateTriggerId is not null;
        // A run claims the inputs without a skill and those of its own skill; the run of a candidate
        // version claims the candidate's inputs only, and the run of a trigger's candidate version
        // those of that candidate only.
        await context.QueuedInputs
            .Where(input => input.ConversationId == conversationId
                && input.ConsumedAt == null
                && input.TriggerCandidate == triggerCandidate
                && (!triggerCandidate || input.TriggerId == candidateTriggerId)
                && (candidate
                    ? input.SkillCandidate && input.SkillId == skillId
                    : !input.SkillCandidate && (input.SkillId == null || input.SkillId == skillId)))
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(input => input.ConsumedByRunId, runId)
                    .SetProperty(input => input.ConsumedAt, now),
                cancellationToken);
        return await context.QueuedInputs
            .AsNoTracking()
            .Where(input => input.ConsumedByRunId == runId)
            .OrderBy(input => input.CreatedAt)
            .ThenBy(input => input.Id)
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// The people whose messages the run claimed, each once: the authors of its inputs that a person
    /// wrote, such as a message typed while the run worked.
    /// </summary>
    public async Task<IReadOnlyList<string>> ListInputAuthorsAsync(Guid runId, CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        return await context.QueuedInputs
            .AsNoTracking()
            .Where(input => input.ConsumedByRunId == runId && input.AuthorActorId != null)
            .Select(input => input.AuthorActorId!)
            .Distinct()
            .OrderBy(author => author)
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Whether the input with <paramref name="inputId"/> was written, consumed or not.
    /// </summary>
    public async Task<bool> QueuedInputExistsAsync(Guid inputId, CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        return await context.QueuedInputs.AnyAsync(input => input.Id == inputId, cancellationToken);
    }

    /// <summary>
    /// Whether an input of <paramref name="triggerId"/> with <paramref name="eventKey"/> still waits
    /// in the thread, so a redelivered event waits once.
    /// </summary>
    public async Task<bool> HasWaitingInputAsync(
        Guid conversationId,
        string triggerId,
        string eventKey,
        CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        return await context.QueuedInputs.AnyAsync(
            input => input.ConversationId == conversationId
                && input.ConsumedAt == null
                && input.TriggerId == triggerId
                && input.EventKey == eventKey,
            cancellationToken);
    }

    /// <summary>
    /// The inputs that wait in the threads of an agent.
    /// </summary>
    public async Task<int> CountAgentWaitingInputsAsync(string agentId, CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        return await context.QueuedInputs
            .Where(input => input.ConsumedAt == null)
            .Join(
                context.Threads.Where(thread => thread.AgentId == agentId),
                input => input.ConversationId,
                thread => thread.ConversationId,
                (input, _) => input.Id)
            .CountAsync(cancellationToken);
    }

    /// <summary>
    /// The oldest waiting input of every thread of an agent that has no queued run to claim it,
    /// oldest first: the inputs a resumed agent starts one run for per thread.
    /// </summary>
    public async Task<IReadOnlyList<(AssistantThread Thread, AssistantQueuedInput Input)>> GetUnclaimedWaitingInputsAsync(
        string agentId,
        int maximum,
        CancellationToken cancellationToken,
        IReadOnlyCollection<string>? heldSkills = null,
        string? onlySkill = null)
    {
        await using var context = contextFactory.CreateDbContext();
        var held = (heldSkills ?? []).ToArray();
        // One group per thread and skill; a queued run of the same skill claims its inputs itself.
        var groups = await context.QueuedInputs
            .Where(input => input.ConsumedAt == null
                // A trigger's candidate lets nothing wait for a resume.
                && !input.TriggerCandidate
                && (input.SkillId == null || !held.Contains(input.SkillId))
                && (onlySkill == null || input.SkillId == onlySkill)
                && !context.Runs.Any(run => run.ConversationId == input.ConversationId
                    && run.Status == NhAssistantRunStatuses.Queued
                    && run.SkillId == input.SkillId))
            .Join(
                context.Threads.Where(thread => thread.AgentId == agentId),
                input => input.ConversationId,
                thread => thread.ConversationId,
                (input, _) => input)
            .GroupBy(input => new { input.ConversationId, input.SkillId })
            .Select(group => new { group.Key.ConversationId, group.Key.SkillId, Oldest = group.Min(input => input.CreatedAt) })
            .OrderBy(item => item.Oldest)
            .Take(maximum)
            .ToListAsync(cancellationToken);
        var result = new List<(AssistantThread Thread, AssistantQueuedInput Input)>(groups.Count);
        foreach (var group in groups)
        {
            var conversationId = group.ConversationId;
            var skillId = group.SkillId;
            var thread = await context.Threads.AsNoTracking()
                .SingleOrDefaultAsync(item => item.ConversationId == conversationId, cancellationToken);
            var input = await context.QueuedInputs.AsNoTracking()
                .Where(item => item.ConversationId == conversationId && item.ConsumedAt == null && item.SkillId == skillId)
                .OrderBy(item => item.CreatedAt)
                .ThenBy(item => item.Id)
                .FirstOrDefaultAsync(cancellationToken);
            if (thread is not null && input is not null)
            {
                result.Add((thread, input));
            }
        }

        return result;
    }

    /// <summary>
    /// Ends the waiting inputs of every thread of an agent without a run, for a stop: the events
    /// that waited are dropped like the ones a stopped agent refuses. Returns how many ended.
    /// </summary>
    public async Task<int> DiscardAgentWaitingInputsAsync(string agentId, CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        var now = DateTimeOffset.UtcNow;
        return await context.QueuedInputs
            .Where(input => input.ConsumedAt == null
                && context.Threads.Any(thread => thread.ConversationId == input.ConversationId && thread.AgentId == agentId))
            .ExecuteUpdateAsync(setters => setters.SetProperty(input => input.ConsumedAt, now), cancellationToken);
    }

    /// <summary>
    /// Ends the inputs that wait in the threads of an agent, or with <paramref name="skillId"/> for
    /// one skill, for no run: the events that waited from a pause, when the agent or skill is
    /// switched off. Inputs in a thread with a run that has not ended stay for that run, which
    /// finishes. Returns how many ended.
    /// </summary>
    public async Task<int> DiscardUnclaimedWaitingInputsAsync(
        string agentId,
        CancellationToken cancellationToken,
        string? skillId = null)
    {
        await using var context = contextFactory.CreateDbContext();
        var now = DateTimeOffset.UtcNow;
        return await context.QueuedInputs
            .Where(input => input.ConsumedAt == null
                && (skillId == null || input.SkillId == skillId)
                && context.Threads.Any(thread => thread.ConversationId == input.ConversationId && thread.AgentId == agentId)
                && !context.Runs.Any(run => run.ConversationId == input.ConversationId
                    && run.Status != NhAssistantRunStatuses.Completed
                    && run.Status != NhAssistantRunStatuses.Failed
                    && run.Status != NhAssistantRunStatuses.Cancelled))
            .ExecuteUpdateAsync(setters => setters.SetProperty(input => input.ConsumedAt, now), cancellationToken);
    }

    /// <summary>
    /// Ends the waiting inputs of one thread without a run, when its subject is gone.
    /// </summary>
    public async Task DiscardWaitingInputsAsync(Guid conversationId, CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        var now = DateTimeOffset.UtcNow;
        await context.QueuedInputs
            .Where(input => input.ConversationId == conversationId && input.ConsumedAt == null)
            .ExecuteUpdateAsync(setters => setters.SetProperty(input => input.ConsumedAt, now), cancellationToken);
    }

    /// <summary>
    /// The waiting inputs of a thread, or with <paramref name="claimableBySkill"/> those a run in
    /// that skill (or without one, for the empty string) would claim.
    /// </summary>
    public async Task<int> CountWaitingInputsAsync(
        Guid conversationId,
        CancellationToken cancellationToken,
        string? claimableBySkill = null)
    {
        await using var context = contextFactory.CreateDbContext();
        var skill = claimableBySkill is { Length: > 0 } ? claimableBySkill : null;
        return await context.QueuedInputs.CountAsync(
            input => input.ConversationId == conversationId
                && input.ConsumedAt == null
                && (claimableBySkill == null || input.SkillId == null || input.SkillId == skill),
            cancellationToken);
    }

    /// <summary>
    /// Ends the waiting inputs of one skill of an agent without a run, for a stop of the skill.
    /// </summary>
    public async Task<int> DiscardSkillWaitingInputsAsync(string agentId, string skillId, CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        var now = DateTimeOffset.UtcNow;
        return await context.QueuedInputs
            .Where(input => input.ConsumedAt == null
                && input.SkillId == skillId
                && context.Threads.Any(thread => thread.ConversationId == input.ConversationId && thread.AgentId == agentId))
            .ExecuteUpdateAsync(setters => setters.SetProperty(input => input.ConsumedAt, now), cancellationToken);
    }

    /// <summary>
    /// Moves the waiting inputs of <paramref name="fromConversationId"/> that wait for
    /// <paramref name="skillId"/>, or for no skill with <see langword="null"/>, to another thread of
    /// the same agent and subject, when the work changes between the live and the shadow thread.
    /// Returns how many moved.
    /// </summary>
    public async Task<int> MoveWaitingInputsAsync(
        Guid fromConversationId,
        Guid toConversationId,
        string? skillId,
        CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        return await context.QueuedInputs
            .Where(input => input.ConversationId == fromConversationId
                && input.ConsumedAt == null
                && input.SkillId == skillId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(input => input.ConversationId, toConversationId), cancellationToken);
    }

    /// <summary>
    /// Moves a run that has not started to the shadow thread <paramref name="toConversationId"/>,
    /// with the waiting inputs of its skill, when its agent or skill went into shadow after it was
    /// queued. Returns <see langword="false"/> when the run started meanwhile.
    /// </summary>
    public async Task<bool> MoveQueuedRunToShadowAsync(
        Guid runId,
        Guid fromConversationId,
        Guid toConversationId,
        string? skillId,
        CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        var moved = await context.Runs
            .Where(run => run.Id == runId
                && run.ConversationId == fromConversationId
                && run.Status == NhAssistantRunStatuses.Queued)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(run => run.ConversationId, toConversationId)
                    .SetProperty(run => run.Shadow, true),
                cancellationToken);
        if (moved == 0)
        {
            return false;
        }

        await context.QueuedInputs
            .Where(input => input.ConversationId == fromConversationId
                && input.ConsumedAt == null
                && input.SkillId == skillId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(input => input.ConversationId, toConversationId), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    /// <summary>
    /// Makes <paramref name="accountableOwnerId"/> answer for the threads of an agent that answers to
    /// a technical account, after an administrator changed the account. Runs and proposals keep the
    /// account they were made with.
    /// </summary>
    /// <summary>
    /// The runs of the agent's threads in one skill and the given versions since
    /// <paramref name="from"/>, until <paramref name="to"/>.
    /// </summary>
    public async Task<IReadOnlyList<AssistantRun>> GetSkillVersionRunsAsync(
        string agentId,
        string skillId,
        IReadOnlyCollection<int> versions,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        return await context.Runs
            .AsNoTracking()
            .Where(run => run.SkillId == skillId
                && run.SkillVersion != null
                && versions.Contains(run.SkillVersion.Value)
                && run.CreatedAt >= from
                && run.CreatedAt < to
                && context.Threads.Any(thread => thread.ConversationId == run.ConversationId && thread.AgentId == agentId))
            .OrderBy(run => run.CreatedAt)
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// The runs of the agent that a version of its data trigger in <paramref name="versions"/>
    /// started in the period, live and candidate, oldest first.
    /// </summary>
    public async Task<IReadOnlyList<AssistantRun>> GetTriggerVersionRunsAsync(
        string agentId,
        string triggerId,
        IReadOnlyCollection<int> versions,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        return await context.Runs
            .AsNoTracking()
            .Where(run => run.TriggerId == triggerId
                && run.TriggerVersion != null
                && versions.Contains(run.TriggerVersion.Value)
                && run.CreatedAt >= from
                && run.CreatedAt < to
                && context.Threads.Any(thread => thread.ConversationId == run.ConversationId && thread.AgentId == agentId))
            .OrderBy(run => run.CreatedAt)
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// The proposals the runs made: their run, tool and status.
    /// </summary>
    public async Task<IReadOnlyList<(Guid RunId, string ToolId, string Status)>> GetProposalsOfRunsAsync(
        IReadOnlyCollection<Guid> runIds,
        CancellationToken cancellationToken)
    {
        if (runIds.Count == 0)
        {
            return [];
        }

        await using var context = contextFactory.CreateDbContext();
        var rows = await context.Approvals
            .AsNoTracking()
            .Where(approval => approval.RunId != null && runIds.Contains(approval.RunId.Value))
            .Select(approval => new { RunId = approval.RunId!.Value, approval.ToolId, approval.Status })
            .ToListAsync(cancellationToken);
        return rows.Select(row => (row.RunId, row.ToolId, row.Status)).ToArray();
    }

    public async Task SetAccountabilityOfThreadAsync(
        Guid conversationId,
        string accountability,
        string accountableOwnerId,
        CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        await context.Threads
            .Where(thread => thread.ConversationId == conversationId)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(thread => thread.Accountability, accountability)
                    .SetProperty(thread => thread.AccountableOwnerId, accountableOwnerId)
                    .SetProperty(thread => thread.UpdatedAt, DateTimeOffset.UtcNow),
                cancellationToken);
    }

    public async Task<int> SetServiceAccountOfThreadsAsync(
        string agentId,
        string accountableOwnerId,
        CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        return await context.Threads
            .Where(thread => thread.AgentId == agentId && thread.Accountability == NhAssistantAccountabilities.ServiceAccount)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(thread => thread.AccountableOwnerId, accountableOwnerId)
                    .SetProperty(thread => thread.UpdatedAt, DateTimeOffset.UtcNow),
                cancellationToken);
    }

    /// <summary>
    /// Gives the inputs a run claimed back to the thread as waiting inputs of
    /// <paramref name="skillId"/>, when the run's rules routed it to a paused skill or to a skill
    /// that works in shadow.
    /// </summary>
    public async Task ReleaseInputsToSkillAsync(Guid runId, string skillId, CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        await context.QueuedInputs
            .Where(input => input.ConsumedByRunId == runId)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(input => input.ConsumedByRunId, (Guid?)null)
                    .SetProperty(input => input.ConsumedAt, (DateTimeOffset?)null)
                    .SetProperty(input => input.SkillId, skillId),
                cancellationToken);
    }

    /// <summary>
    /// The waiting inputs of one skill of an agent.
    /// </summary>
    public async Task<int> CountSkillWaitingInputsAsync(string agentId, string skillId, CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        return await context.QueuedInputs
            .Where(input => input.ConsumedAt == null && input.SkillId == skillId)
            .Join(
                context.Threads.Where(thread => thread.AgentId == agentId),
                input => input.ConversationId,
                thread => thread.ConversationId,
                (input, _) => input.Id)
            .CountAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<AssistantApproval>> GetThreadApprovalsAsync(
        Guid conversationId,
        int maximum,
        CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        return await context.Approvals
            .AsNoTracking()
            .Where(approval => approval.ConversationId == conversationId)
            .OrderByDescending(approval => approval.CreatedAt)
            .Take(maximum)
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// The proposals of one thread that wait for a decision and have not expired, oldest first.
    /// </summary>
    public async Task<IReadOnlyList<AssistantApproval>> GetOpenThreadApprovalsAsync(
        Guid conversationId,
        int maximum,
        CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        // A proposal expires on the application's clock.
        var now = _applicationClock.GetUtcNow();
        return await context.Approvals
            .AsNoTracking()
            .Where(approval => approval.ConversationId == conversationId
                && approval.Status == NhAssistantApprovalStatuses.Pending
                && approval.ExpiresAt > now)
            .OrderBy(approval => approval.CreatedAt)
            .ThenBy(approval => approval.Id)
            .Take(maximum)
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// How many proposals wait for a decision and have not expired in each of the threads; threads
    /// without one are left out.
    /// </summary>
    public async Task<IReadOnlyDictionary<Guid, int>> CountOpenThreadApprovalsAsync(
        IReadOnlyCollection<Guid> conversationIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(conversationIds);
        if (conversationIds.Count == 0)
        {
            return new Dictionary<Guid, int>();
        }

        var ids = conversationIds.Distinct().ToArray();
        // A proposal expires on the application's clock.
        var now = _applicationClock.GetUtcNow();
        await using var context = contextFactory.CreateDbContext();
        var counts = await context.Approvals
            .AsNoTracking()
            .Where(approval => ids.Contains(approval.ConversationId)
                && approval.Status == NhAssistantApprovalStatuses.Pending
                && approval.ExpiresAt > now)
            .GroupBy(approval => approval.ConversationId)
            .Select(group => new { ConversationId = group.Key, Count = group.Count() })
            .ToListAsync(cancellationToken);
        return counts.ToDictionary(item => item.ConversationId, item => item.Count);
    }

    /// <summary>
    /// Per thread its open proposals, pending and not expired: those it holds and those its runs
    /// placed at other objects, with when the newest of them was made, in one query; threads without
    /// open proposals are left out.
    /// </summary>
    public async Task<IReadOnlyDictionary<Guid, (int Count, DateTimeOffset Newest)>> GetOpenProposalStatsAsync(
        IReadOnlyCollection<Guid> conversationIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(conversationIds);
        if (conversationIds.Count == 0)
        {
            return new Dictionary<Guid, (int, DateTimeOffset)>();
        }

        var ids = conversationIds.Distinct().ToArray();
        // A proposal expires on the application's clock.
        var now = _applicationClock.GetUtcNow();
        await using var context = contextFactory.CreateDbContext();
        var stats = await context.Approvals
            .AsNoTracking()
            .Where(approval => approval.Status == NhAssistantApprovalStatuses.Pending
                && approval.ExpiresAt > now
                && (ids.Contains(approval.ConversationId)
                    || (approval.PlacedFromConversationId != null && ids.Contains(approval.PlacedFromConversationId.Value))))
            .GroupBy(approval => approval.PlacedFromConversationId ?? approval.ConversationId)
            .Select(group => new
            {
                ConversationId = group.Key,
                Count = group.Count(),
                Newest = group.Max(approval => approval.CreatedAt)
            })
            .ToListAsync(cancellationToken);
        return stats.ToDictionary(item => item.ConversationId, item => (item.Count, item.Newest));
    }

    /// <summary>
    /// The newest run of each of the threads, by when it was created; threads without runs are left
    /// out.
    /// </summary>
    public async Task<IReadOnlyList<AssistantRun>> GetLatestRunsAsync(
        IReadOnlyCollection<Guid> conversationIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(conversationIds);
        if (conversationIds.Count == 0)
        {
            return [];
        }

        var ids = conversationIds.Distinct().ToArray();
        await using var context = contextFactory.CreateDbContext();
        return await context.Runs
            .AsNoTracking()
            .Where(run => ids.Contains(run.ConversationId))
            .GroupBy(run => run.ConversationId)
            .Select(group => group
                .OrderByDescending(run => run.CreatedAt)
                .ThenByDescending(run => run.Id)
                .First())
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<AssistantApproval>> GetRunApprovalsAsync(
        Guid runId,
        CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        return await context.Approvals
            .AsNoTracking()
            .Where(approval => approval.RunId == runId)
            .OrderBy(approval => approval.CreatedAt)
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// How many proposals the run placed at other objects than its own, in any status.
    /// </summary>
    public async Task<int> CountPlacedProposalsAsync(Guid runId, CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        return await context.Approvals
            .AsNoTracking()
            .CountAsync(approval => approval.RunId == runId && approval.PlacedFromConversationId != null, cancellationToken);
    }

    /// <summary>
    /// The agent threads of the whole application (<see cref="NhAiSubjectRef.SystemType"/>) with a
    /// pending, unexpired agent proposal, ordered by subject, agent and thread, at most
    /// <paramref name="maximum"/>. The inbox asks the caller's live role on each subject and whether
    /// the caller passes each agent's view policy.
    /// </summary>
    public async Task<IReadOnlyList<NhAssistantSystemThread>> GetSystemThreadsWithPendingProposalsAsync(
        int maximum,
        CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        // A proposal expires on the application's clock.
        var now = _applicationClock.GetUtcNow();
        var threads = await (
                from approval in context.Approvals.AsNoTracking()
                join thread in context.Threads.AsNoTracking() on approval.ConversationId equals thread.ConversationId
                where thread.SubjectType == NhAiSubjectRef.SystemType
                    && approval.Status == NhAssistantApprovalStatuses.Pending
                    && approval.ExpiresAt > now
                    && (approval.Mode != NhAssistantApprovalModes.Inline || approval.RunId != null)
                select new { thread.ConversationId, thread.SubjectId, thread.AgentId })
            .Distinct()
            .OrderBy(thread => thread.SubjectId)
            .ThenBy(thread => thread.AgentId)
            .ThenBy(thread => thread.ConversationId)
            .Take(maximum)
            .ToListAsync(cancellationToken);
        return threads
            .Select(thread => new NhAssistantSystemThread(thread.ConversationId, thread.SubjectId, thread.AgentId))
            .ToArray();
    }

    /// <summary>
    /// The agent threads of the whole application about <paramref name="subjectIds"/>, ordered by
    /// subject, agent and thread, at most <paramref name="maximum"/>, for a listing that decides
    /// per agent who may see them.
    /// </summary>
    public async Task<IReadOnlyList<NhAssistantSystemThread>> GetSystemThreadsAsync(
        IReadOnlyCollection<string> subjectIds,
        int maximum,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(subjectIds);
        await using var context = contextFactory.CreateDbContext();
        var threads = await context.Threads
            .AsNoTracking()
            .Where(thread => thread.SubjectType == NhAiSubjectRef.SystemType && subjectIds.Contains(thread.SubjectId))
            .OrderBy(thread => thread.SubjectId)
            .ThenBy(thread => thread.AgentId)
            .ThenBy(thread => thread.ConversationId)
            .Select(thread => new { thread.ConversationId, thread.SubjectId, thread.AgentId })
            .Take(maximum)
            .ToListAsync(cancellationToken);
        return threads
            .Select(thread => new NhAssistantSystemThread(thread.ConversationId, thread.SubjectId, thread.AgentId))
            .ToArray();
    }

    /// <summary>
    /// The live (not shadow) agent threads of the whole application, ordered by subject, agent and
    /// thread, at most <paramref name="maximum"/>, for the people who follow the work of its agents
    /// live.
    /// </summary>
    public async Task<IReadOnlyList<NhAssistantSystemThread>> GetLiveSystemThreadsAsync(
        int maximum,
        CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        var threads = await context.Threads
            .AsNoTracking()
            .Where(thread => thread.SubjectType == NhAiSubjectRef.SystemType && !thread.Shadow)
            .OrderBy(thread => thread.SubjectId)
            .ThenBy(thread => thread.AgentId)
            .ThenBy(thread => thread.ConversationId)
            .Select(thread => new { thread.ConversationId, thread.SubjectId, thread.AgentId })
            .Take(maximum)
            .ToListAsync(cancellationToken);
        return threads
            .Select(thread => new NhAssistantSystemThread(thread.ConversationId, thread.SubjectId, thread.AgentId))
            .ToArray();
    }

    /// <summary>
    /// The people of a thread a notification about its proposals may reach when its subject lists no
    /// members, as the whole application does: those who take part in its conversation, then those who
    /// decided its proposals, most recent first, each once and at most <paramref name="maximum"/>.
    /// </summary>
    public async Task<IReadOnlyList<string>> GetThreadPeopleAsync(
        Guid conversationId,
        int maximum,
        CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        var participants = await context.Participants
            .AsNoTracking()
            .Where(participant => participant.ConversationId == conversationId)
            .OrderByDescending(participant => participant.JoinedAt)
            .Select(participant => participant.ActorId)
            .Take(maximum)
            .ToListAsync(cancellationToken);
        var deciders = await context.Approvals
            .AsNoTracking()
            .Where(approval => approval.ConversationId == conversationId
                && approval.DecidedByActorId != null
                && approval.DecidedAt != null)
            .OrderByDescending(approval => approval.DecidedAt)
            .Select(approval => approval.DecidedByActorId!)
            .Take(maximum * 4)
            .ToListAsync(cancellationToken);
        return participants
            .Concat(deciders)
            .Distinct(StringComparer.Ordinal)
            .Take(maximum)
            .ToArray();
    }

    /// <summary>
    /// Moves an approval from one of <paramref name="expected"/> to <paramref name="status"/> and
    /// applies <paramref name="decision"/> fields. Returns false when another actor or worker moved
    /// it first.
    /// </summary>
    public async Task<bool> TryTransitionApprovalAsync(
        Guid approvalId,
        IReadOnlyCollection<string> expected,
        string status,
        NhAssistantApprovalTransition decision,
        CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        var updated = await context.Approvals
            .Where(approval => approval.Id == approvalId && expected.Contains(approval.Status))
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(approval => approval.Status, status)
                    .SetProperty(
                        approval => approval.DecidedByActorId,
                        approval => decision.DecidedByActorId ?? approval.DecidedByActorId)
                    .SetProperty(
                        approval => approval.DecidedAt,
                        approval => decision.DecidedAt ?? approval.DecidedAt)
                    .SetProperty(
                        approval => approval.ApprovalExpiresAt,
                        approval => decision.ApprovalExpiresAt ?? approval.ApprovalExpiresAt)
                    .SetProperty(approval => approval.Reason, approval => decision.Reason ?? approval.Reason)
                    .SetProperty(
                        approval => approval.ExecutionOperationId,
                        approval => decision.ClearExecutionOperation
                            ? null
                            : decision.ExecutionOperationId ?? approval.ExecutionOperationId)
                    .SetProperty(
                        approval => approval.ExecutionAttempts,
                        approval => approval.ExecutionAttempts + (decision.CountExecutionAttempt ? 1 : 0))
                    .SetProperty(
                        approval => approval.ExecutedAt,
                        approval => decision.ClearExecutedAt
                            ? null
                            : decision.ExecutedAt ?? approval.ExecutedAt)
                    .SetProperty(
                        approval => approval.ExecutionResultCode,
                        approval => decision.ExecutionResultCode ?? approval.ExecutionResultCode)
                    .SetProperty(
                        approval => approval.ExecutionReportDueAt,
                        approval => decision.ExecutionReportDueAt ?? approval.ExecutionReportDueAt)
                    .SetProperty(
                        approval => approval.ExpiresAt,
                        approval => decision.ExpiresAt ?? approval.ExpiresAt)
                    .SetProperty(
                        approval => approval.PolicyJson,
                        approval => decision.PolicyJson ?? approval.PolicyJson)
                    .SetProperty(
                        approval => approval.EscalatedAt,
                        approval => decision.EscalatedAt ?? approval.EscalatedAt)
                    .SetProperty(
                        approval => approval.NextReminderAt,
                        approval => decision.ResetReminders
                            ? decision.NextReminderAt
                            : decision.NextReminderAt ?? approval.NextReminderAt)
                    .SetProperty(
                        approval => approval.RemindersSent,
                        approval => decision.ResetReminders ? 0 : approval.RemindersSent)
                    .SetProperty(approval => approval.StaleKind, approval => decision.StaleKind ?? approval.StaleKind)
                    .SetProperty(
                        approval => approval.StaleByActorId,
                        approval => decision.StaleByActorId ?? approval.StaleByActorId)
                    .SetProperty(
                        approval => approval.SupersededByApprovalId,
                        approval => decision.SupersededByApprovalId ?? approval.SupersededByApprovalId)
                    .SetProperty(
                        approval => approval.StaleExplanationsJson,
                        approval => decision.StaleExplanationsJson ?? approval.StaleExplanationsJson)
                    .SetProperty(approval => approval.ConcurrencyStamp, Guid.NewGuid()),
                cancellationToken);
        if (updated != 1)
        {
            return false;
        }

        if (ClosedCallStatus(status) is { } callStatus)
        {
            await CloseWaitingToolCallAsync(context, approvalId, callStatus, status, cancellationToken);
        }

        return true;
    }

    /// <summary>
    /// The status a tool call still waiting for its proposal gets when the proposal closes without
    /// running it, or <see langword="null"/> when the proposal stays open or its execution sets it.
    /// </summary>
    private static string? ClosedCallStatus(string approvalStatus)
    {
        return approvalStatus switch
        {
            NhAssistantApprovalStatuses.Rejected => NhAssistantToolCallStatuses.Rejected,
            NhAssistantApprovalStatuses.Expired
                or NhAssistantApprovalStatuses.Withdrawn
                or NhAssistantApprovalStatuses.Stale => NhAssistantToolCallStatuses.NotExecuted,
            NhAssistantApprovalStatuses.ExecutionFailed => NhAssistantToolCallStatuses.Failed,
            // Only the application's report ends a call that still waits: an execution in the tool's
            // function ended its call before the proposal closed.
            NhAssistantApprovalStatuses.Executed => NhAssistantToolCallStatuses.Succeeded,
            _ => null
        };
    }

    /// <summary>
    /// Ends the tool call of a closed proposal that still waits for approval, so the thread no longer
    /// shows it as waiting. A call that a person's turn or an execution already ended keeps its status.
    /// </summary>
    private static async Task CloseWaitingToolCallAsync(
        NhAssistantDbContext context,
        Guid approvalId,
        string callStatus,
        string approvalStatus,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var resultCode = "assistant-proposal-" + approvalStatus;
        await context.ToolInvocations
            .Where(invocation => invocation.Status == NhAssistantToolCallStatuses.AwaitingApproval
                && context.Approvals.Any(approval => approval.Id == approvalId && approval.ToolInvocationId == invocation.Id))
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(invocation => invocation.Status, callStatus)
                    .SetProperty(invocation => invocation.ResultCode, resultCode)
                    .SetProperty(invocation => invocation.CompletedAt, now),
                cancellationToken);
    }

    /// <summary>
    /// Records a sent reminder once and schedules the next one, or none.
    /// </summary>
    public async Task<bool> TryRecordReminderAsync(
        Guid approvalId,
        int expectedRemindersSent,
        DateTimeOffset? nextReminderAt,
        CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        var updated = await context.Approvals
            .Where(approval => approval.Id == approvalId
                && approval.Status == NhAssistantApprovalStatuses.Pending
                && approval.RemindersSent == expectedRemindersSent)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(approval => approval.RemindersSent, expectedRemindersSent + 1)
                    .SetProperty(approval => approval.NextReminderAt, nextReminderAt)
                    .SetProperty(approval => approval.ConcurrencyStamp, Guid.NewGuid()),
                cancellationToken);
        return updated == 1;
    }

    /// <summary>
    /// Proposals that wait for the application's report and whose report deadline
    /// <paramref name="now"/> of the application's clock reached, earliest deadline first.
    /// </summary>
    public async Task<IReadOnlyList<AssistantApproval>> GetOverdueExecutionReportsAsync(
        DateTimeOffset now,
        int maximum,
        CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        return await context.Approvals
            .AsNoTracking()
            .Where(approval => approval.Status == NhAssistantApprovalStatuses.AwaitingApplication
                && approval.ExecutionReportDueAt != null
                && approval.ExecutionReportDueAt <= now)
            .OrderBy(approval => approval.ExecutionReportDueAt)
            .ThenBy(approval => approval.Id)
            .Take(maximum)
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Waiting deferred proposals whose next reminder is due, earliest first.
    /// </summary>
    public async Task<IReadOnlyList<AssistantApproval>> GetDueRemindersAsync(
        DateTimeOffset now,
        int maximum,
        CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        return await context.Approvals
            .AsNoTracking()
            .Where(approval => approval.Status == NhAssistantApprovalStatuses.Pending
                && approval.NextReminderAt != null
                && approval.NextReminderAt <= now)
            .OrderBy(approval => approval.NextReminderAt)
            .ThenBy(approval => approval.Id)
            .Take(maximum)
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Pending proposals of agent runs and of consultations, oldest expiry first, for the maintenance
    /// job (CP24). Proposals of a person's own turn wait in the conversation and are left out.
    /// </summary>
    public async Task<IReadOnlyList<AssistantApproval>> GetPendingThreadApprovalsAsync(
        DateTimeOffset expiringBefore,
        int maximum,
        CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        return await context.Approvals
            .AsNoTracking()
            .Where(approval => approval.Status == NhAssistantApprovalStatuses.Pending
                && (approval.RunId != null || approval.ConsultationId != null)
                && approval.ExpiresAt <= expiringBefore)
            .OrderBy(approval => approval.ExpiresAt)
            .Take(maximum)
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// The pending proposals of agent runs and consultations about a subject, in every scope, with
    /// their thread. Proposals of a person's own turn wait in the conversation and are left out.
    /// </summary>
    public async Task<IReadOnlyList<NhAssistantOpenProposalRow>> GetOpenProposalsForSubjectAsync(
        NhAiSubjectRef subject,
        int maximum,
        CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        var rows = await OpenAgentProposals(context)
            .Join(
                context.Threads.Where(thread => thread.SubjectType == subject.Type && thread.SubjectId == subject.Id),
                approval => approval.ConversationId,
                thread => thread.ConversationId,
                (approval, thread) => new { Approval = approval, Thread = thread })
            .OrderBy(row => row.Approval.CreatedAt)
            .Take(maximum)
            .ToListAsync(cancellationToken);
        return rows.Select(row => new NhAssistantOpenProposalRow(row.Approval, row.Thread)).ToArray();
    }

    /// <summary>
    /// The pending proposals of agent runs and consultations that change one of
    /// <paramref name="keys"/> within <paramref name="scopeKey"/>, with their thread.
    /// </summary>
    public async Task<IReadOnlyList<NhAssistantOpenProposalRow>> GetOpenProposalsByKeysAsync(
        string scopeKey,
        IReadOnlyCollection<NhAiProposalKey> keys,
        int maximum,
        CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        var rows = new Dictionary<Guid, NhAssistantOpenProposalRow>();
        foreach (var group in keys.Distinct().GroupBy(key => key.Type, StringComparer.Ordinal))
        {
            var type = group.Key;
            var ids = group.Select(key => key.Id).ToArray();
            var approvalIds = context.ApprovalKeys
                .Where(key => key.ScopeKey == scopeKey && key.KeyType == type && ids.Contains(key.KeyId))
                .Select(key => key.ApprovalId);
            var found = await OpenAgentProposals(context)
                .Where(approval => approvalIds.Contains(approval.Id))
                .Join(
                    context.Threads,
                    approval => approval.ConversationId,
                    thread => thread.ConversationId,
                    (approval, thread) => new { Approval = approval, Thread = thread })
                .OrderBy(row => row.Approval.CreatedAt)
                .Take(maximum)
                .ToListAsync(cancellationToken);
            foreach (var row in found)
            {
                rows.TryAdd(row.Approval.Id, new NhAssistantOpenProposalRow(row.Approval, row.Thread));
            }
        }

        return rows.Values
            .OrderBy(row => row.Approval.CreatedAt)
            .Take(maximum)
            .ToArray();
    }

    /// <summary>
    /// Whether an agent run or consultation has an open proposal about the subject, so a
    /// revalidation for it is worth queuing.
    /// </summary>
    public async Task<bool> HasOpenProposalsForSubjectAsync(NhAiSubjectRef subject, CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        return await OpenAgentProposals(context)
            .Join(
                context.Threads.Where(thread => thread.SubjectType == subject.Type && thread.SubjectId == subject.Id),
                approval => approval.ConversationId,
                thread => thread.ConversationId,
                (approval, _) => approval.Id)
            .AnyAsync(cancellationToken);
    }

    /// <summary>
    /// Whether an agent run or consultation has an open proposal that changes one of
    /// <paramref name="keys"/> within <paramref name="scopeKey"/>.
    /// </summary>
    public async Task<bool> HasOpenProposalsByKeysAsync(
        string scopeKey,
        IReadOnlyCollection<NhAiProposalKey> keys,
        CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        foreach (var group in keys.Distinct().GroupBy(key => key.Type, StringComparer.Ordinal))
        {
            var type = group.Key;
            var ids = group.Select(key => key.Id).ToArray();
            var open = await context.ApprovalKeys
                .Where(key => key.ScopeKey == scopeKey && key.KeyType == type && ids.Contains(key.KeyId))
                .Join(
                    OpenAgentProposals(context),
                    key => key.ApprovalId,
                    approval => approval.Id,
                    (key, _) => key.ApprovalId)
                .AnyAsync(cancellationToken);
            if (open)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The oldest pending, approved or executing proposal of the agent within the scope that has the
    /// same identity, also one that waits for the application's report, or <see langword="null"/>.
    /// </summary>
    public async Task<AssistantApproval?> FindOpenProposalAsync(
        string scopeKey,
        string agentId,
        string toolId,
        string argumentsHash,
        CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        return await AgentProposals(context, scopeKey, agentId)
            .Where(approval => approval.ToolId == toolId
                && approval.ArgumentsHash == argumentsHash
                && OpenProposalStatuses.Contains(approval.Status)
                // A proposal the asker decides in their own conversation takes no part in duplicate
                // detection.
                && !(approval.ConsultationId != null && approval.Mode == NhAssistantApprovalModes.Inline))
            .OrderBy(approval => approval.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);
    }

    /// <summary>
    /// The latest proposal of the agent within the scope with the same identity that people rejected
    /// since <paramref name="since"/>, or <see langword="null"/>.
    /// </summary>
    public async Task<AssistantApproval?> FindRecentRejectionAsync(
        string scopeKey,
        string agentId,
        string toolId,
        string argumentsHash,
        DateTimeOffset since,
        CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        return await AgentProposals(context, scopeKey, agentId)
            .Where(approval => approval.ToolId == toolId
                && approval.ArgumentsHash == argumentsHash
                && approval.Status == NhAssistantApprovalStatuses.Rejected
                && approval.DecidedAt >= since
                // The asker declining their own consultation's proposal is no rejection of the agent's work.
                && !(approval.ConsultationId != null && approval.Mode == NhAssistantApprovalModes.Inline))
            .OrderByDescending(approval => approval.DecidedAt)
            .FirstOrDefaultAsync(cancellationToken);
    }

    /// <summary>
    /// The proposals of the agent and tool within the scope that change exactly
    /// <paramref name="keys"/>, in one of <paramref name="statuses"/>, newest first; rejections only
    /// when decided since <paramref name="decidedSince"/>.
    /// </summary>
    public async Task<IReadOnlyList<AssistantApproval>> FindProposalsForObjectsAsync(
        string scopeKey,
        string agentId,
        string toolId,
        IReadOnlyCollection<NhAiProposalKey> keys,
        IReadOnlyCollection<string> statuses,
        DateTimeOffset? decidedSince,
        CancellationToken cancellationToken)
    {
        if (keys.Count == 0)
        {
            return [];
        }

        await using var context = contextFactory.CreateDbContext();
        var first = keys.First();
        var candidates = await AgentProposals(context, scopeKey, agentId)
            .Where(approval => approval.ToolId == toolId
                && statuses.Contains(approval.Status)
                && (decidedSince == null || approval.DecidedAt >= decidedSince)
                && context.ApprovalKeys.Any(key => key.ApprovalId == approval.Id
                    && key.ScopeKey == scopeKey
                    && key.KeyType == first.Type
                    && key.KeyId == first.Id))
            .OrderByDescending(approval => approval.CreatedAt)
            .Take(20)
            .ToListAsync(cancellationToken);
        if (candidates.Count == 0)
        {
            return [];
        }

        var candidateIds = candidates.Select(approval => approval.Id).ToArray();
        var keysByApproval = (await context.ApprovalKeys
                .AsNoTracking()
                .Where(key => candidateIds.Contains(key.ApprovalId))
                .Select(key => new { key.ApprovalId, key.KeyType, key.KeyId })
                .ToListAsync(cancellationToken))
            .ToLookup(key => key.ApprovalId);
        var wanted = keys.Select(key => (key.Type, key.Id)).ToHashSet();
        return candidates
            .Where(approval => keysByApproval[approval.Id].Select(key => (key.KeyType, key.KeyId)).ToHashSet().SetEquals(wanted))
            .ToArray();
    }

    /// <summary>
    /// The objects each of <paramref name="approvalIds"/> changes, as their guards named them.
    /// </summary>
    public async Task<ILookup<Guid, NhAiProposalKey>> GetApprovalKeysAsync(
        IReadOnlyCollection<Guid> approvalIds,
        CancellationToken cancellationToken)
    {
        if (approvalIds.Count == 0)
        {
            return Array.Empty<NhAiProposalKey>().ToLookup(_ => Guid.Empty);
        }

        await using var context = contextFactory.CreateDbContext();
        var ids = approvalIds.Distinct().ToArray();
        var keys = await context.ApprovalKeys
            .AsNoTracking()
            .Where(key => ids.Contains(key.ApprovalId))
            .OrderBy(key => key.KeyType)
            .ThenBy(key => key.KeyId)
            .Select(key => new { key.ApprovalId, key.KeyType, key.KeyId })
            .ToListAsync(cancellationToken);
        return keys.ToLookup(key => key.ApprovalId, key => new NhAiProposalKey(key.KeyType, key.KeyId));
    }

    /// <summary>
    /// The proposals of agent threads among <paramref name="approvalIds"/>, each with its thread.
    /// Unknown ids and proposals outside agent threads are left out.
    /// </summary>
    public async Task<IReadOnlyList<NhAssistantInboxRow>> GetThreadApprovalsByIdAsync(
        IReadOnlyCollection<Guid> approvalIds,
        CancellationToken cancellationToken)
    {
        if (approvalIds.Count == 0)
        {
            return [];
        }

        await using var context = contextFactory.CreateDbContext();
        var ids = approvalIds.Distinct().ToArray();
        var rows = await (
                from approval in context.Approvals.AsNoTracking()
                join thread in context.Threads.AsNoTracking() on approval.ConversationId equals thread.ConversationId
                where ids.Contains(approval.Id)
                select new { approval, thread })
            .ToListAsync(cancellationToken);
        return rows.Select(row => new NhAssistantInboxRow(row.approval, row.thread)).ToList();
    }

    private static readonly string[] OpenProposalStatuses =
    [
        NhAssistantApprovalStatuses.Pending,
        NhAssistantApprovalStatuses.Approved,
        NhAssistantApprovalStatuses.Executing,
        NhAssistantApprovalStatuses.AwaitingApplication
    ];

    private static IQueryable<AssistantApproval> AgentProposals(NhAssistantDbContext context, string scopeKey, string agentId)
    {
        return context.Approvals
            .AsNoTracking()
            .Where(approval => context.Threads.Any(thread => thread.ConversationId == approval.ConversationId
                && thread.ScopeKey == scopeKey
                && thread.AgentId == agentId));
    }

    private static IQueryable<AssistantApproval> OpenAgentProposals(NhAssistantDbContext context)
    {
        return context.Approvals
            .AsNoTracking()
            .Where(approval => approval.Status == NhAssistantApprovalStatuses.Pending
                && (approval.Mode != NhAssistantApprovalModes.Inline || approval.RunId != null));
    }

    /// <summary>
    /// Counts, per subject, the open proposals and active runs of the agent threads
    /// <paramref name="actorId"/> may see according to the member snapshot, and the proposals they
    /// may decide as the inbox counts them. Subjects without visible work are left out; shadow
    /// threads count only with <paramref name="includeShadow"/>.
    /// </summary>
    /// <param name="liveRoles">The caller's live roles on subjects that list no members, such as the
    /// whole application; they replace the member snapshot for those subjects.</param>
    public async Task<IReadOnlyList<NhAssistantSubjectSummaryRow>> SummarizeSubjectsAsync(
        string actorId,
        string subjectType,
        IReadOnlyCollection<string> subjectIds,
        IReadOnlyCollection<string> hiddenAgentIds,
        CancellationToken cancellationToken,
        bool includeShadow = false,
        IReadOnlyDictionary<string, NhAiSubjectRole>? liveRoles = null,
        IReadOnlyCollection<Guid>? visibleConversationIds = null)
    {
        await using var context = contextFactory.CreateDbContext();
        // A proposal expires on the application's clock.
        var now = _applicationClock.GetUtcNow();
        // The whole application counts only the threads of agents whose view policy the caller passes.
        var restrictThreads = visibleConversationIds is not null;
        var visible = (visibleConversationIds ?? []).ToArray();
        var readerIds = (liveRoles ?? new Dictionary<string, NhAiSubjectRole>())
            .Where(entry => entry.Value >= NhAiSubjectRole.Reader)
            .Select(entry => entry.Key)
            .ToArray();
        var approverIds = (liveRoles ?? new Dictionary<string, NhAiSubjectRole>())
            .Where(entry => entry.Value >= NhAiSubjectRole.Approver)
            .Select(entry => entry.Key)
            .ToArray();
        var memberships = context.SubjectMembers
            .Where(member => member.ActorId == actorId
                && member.SubjectType == subjectType
                && subjectIds.Contains(member.SubjectId));
        var threads = context.Threads
            .AsNoTracking()
            .Where(thread => thread.SubjectType == subjectType
                && subjectIds.Contains(thread.SubjectId)
                && !hiddenAgentIds.Contains(thread.AgentId)
                && (includeShadow || !thread.Shadow)
                && (memberships.Any(member => member.ScopeKey == thread.ScopeKey && member.SubjectId == thread.SubjectId)
                    || readerIds.Contains(thread.SubjectId))
                && (!restrictThreads || visible.Contains(thread.ConversationId)));
        var pending =
            from approval in context.Approvals.AsNoTracking()
            join thread in threads on approval.ConversationId equals thread.ConversationId
            where approval.Status == NhAssistantApprovalStatuses.Pending && approval.ExpiresAt > now
            select new { approval, thread };

        var open = await pending
            .GroupBy(row => row.thread.SubjectId)
            .Select(group => new { SubjectId = group.Key, Count = group.Count() })
            .ToDictionaryAsync(row => row.SubjectId, row => row.Count, cancellationToken);
        var toDecide = await pending
            .Where(row => (row.approval.InitiatorActorId == actorId && row.approval.PlacedFromConversationId == null)
                || approverIds.Contains(row.thread.SubjectId)
                || memberships.Any(member => member.ScopeKey == row.thread.ScopeKey
                    && member.SubjectId == row.thread.SubjectId
                    && member.Role >= NhAiSubjectRole.Approver))
            .GroupBy(row => row.thread.SubjectId)
            .Select(group => new { SubjectId = group.Key, Count = group.Count() })
            .ToDictionaryAsync(row => row.SubjectId, row => row.Count, cancellationToken);
        var running = await (
                from run in context.Runs.AsNoTracking()
                join thread in threads on run.ConversationId equals thread.ConversationId
                where ActiveRunStatuses.Contains(run.Status)
                select thread.SubjectId)
            .Distinct()
            .ToListAsync(cancellationToken);

        return open.Keys
            .Concat(running)
            .Distinct(StringComparer.Ordinal)
            .Select(subjectId => new NhAssistantSubjectSummaryRow(
                subjectId,
                open.GetValueOrDefault(subjectId),
                toDecide.GetValueOrDefault(subjectId),
                running.Contains(subjectId, StringComparer.Ordinal)))
            .ToList();
    }

    /// <summary>
    /// The pending proposals <paramref name="actorId"/> may decide according to the member
    /// snapshot: as an approver of the subject, or as the initiator of the run that made it in its own
    /// thread. The whole application lists no members, so its subjects among
    /// <paramref name="systemApproverSubjectIds"/>, where the caller's live role is approver, count as
    /// well. Each decision is checked live again.
    /// </summary>
    /// <remarks>
    /// Flat, the page lists the proposals that expire first. With <paramref name="grouped"/> the
    /// proposals are grouped by the parent each recorded when it was made, and a proposal without a
    /// parent by its own subject, so a parent's own proposals and those of its children form one
    /// group. Groups come in the order of their first expiry, a group's own subject first and then its
    /// other subjects by their first expiry, the proposals of one run together, and within that by
    /// expiry. Each group and subject is contiguous across pages; the page tells per group and subject
    /// how many proposals it has in total and whether it continues on the page before or after.
    /// </remarks>
    public async Task<NhAssistantInboxPage> ListInboxAsync(
        string actorId,
        NhAssistantInboxFilter filter,
        int page,
        int itemsPerPage,
        CancellationToken cancellationToken,
        NhAssistantSystemInbox? system = null,
        bool grouped = false)
    {
        await using var context = contextFactory.CreateDbContext();
        // A proposal expires on the application's clock.
        var now = _applicationClock.GetUtcNow();
        var systemApprover = (system?.ApproverConversationIds ?? []).ToArray();
        var systemViewer = (system?.ViewerConversationIds ?? []).ToArray();
        var approverSubjects = context.SubjectMembers
            .Where(member => member.ActorId == actorId && member.Role >= NhAiSubjectRole.Approver);
        var query =
            from approval in context.Approvals.AsNoTracking()
            join thread in context.Threads.AsNoTracking() on approval.ConversationId equals thread.ConversationId
            where approval.Status == NhAssistantApprovalStatuses.Pending
                && approval.ExpiresAt > now
                && ((thread.SubjectType != NhAiSubjectRef.SystemType
                        && (approverSubjects.Any(member => member.ScopeKey == thread.ScopeKey
                                && member.SubjectType == thread.SubjectType
                                && member.SubjectId == thread.SubjectId)
                            // A proposal placed at another object waits for that object's approvers only.
                            || (approval.InitiatorActorId == actorId && approval.PlacedFromConversationId == null)))
                    // The whole application lists no members: its approvers and initiators who pass the
                    // agent's view policy, decided live per thread.
                    || (thread.SubjectType == NhAiSubjectRef.SystemType
                        && (systemApprover.Contains(thread.ConversationId)
                            || (approval.InitiatorActorId == actorId
                                && approval.PlacedFromConversationId == null
                                && systemViewer.Contains(thread.ConversationId)))))
            select new { approval, thread };
        if (filter.SubjectType is { } subjectType)
        {
            query = query.Where(row => row.thread.SubjectType == subjectType);
        }
        if (filter.AgentId is { } agentId)
        {
            query = query.Where(row => row.thread.AgentId == agentId);
        }
        if (filter.DivisionId is { } divisionId)
        {
            query = query.Where(row => row.thread.DivisionId == divisionId);
        }
        if (filter.RunId is { } runId)
        {
            query = query.Where(row => row.approval.RunId == runId);
        }

        var total = await query.CountAsync(cancellationToken);
        if (!grouped)
        {
            var rows = await query
                .OrderBy(row => row.approval.ExpiresAt)
                .ThenBy(row => row.approval.Id)
                .Skip((page - 1) * itemsPerPage)
                .Take(itemsPerPage)
                .ToListAsync(cancellationToken);
            return new NhAssistantInboxPage(rows.Select(row => new NhAssistantInboxRow(row.approval, row.thread)).ToList(), total);
        }

        // A proposal without a parent groups by its own subject. Scope keys keep equal ids of
        // different tenants or divisions apart.
        var keyed =
            from row in query
            select new
            {
                row.approval,
                row.thread,
                GroupType = row.approval.ParentSubjectType ?? row.thread.SubjectType,
                GroupId = row.approval.ParentSubjectId ?? row.thread.SubjectId
            };
        var groups =
            from row in keyed
            group row by new { row.thread.ScopeKey, row.GroupType, row.GroupId } into grouping
            select new
            {
                grouping.Key.ScopeKey,
                grouping.Key.GroupType,
                grouping.Key.GroupId,
                Total = grouping.Count(),
                FirstExpiry = grouping.Min(item => item.approval.ExpiresAt)
            };
        var subjects =
            from row in keyed
            group row by new { row.thread.ScopeKey, row.GroupType, row.GroupId, row.thread.SubjectType, row.thread.SubjectId } into grouping
            select new
            {
                grouping.Key.ScopeKey,
                grouping.Key.GroupType,
                grouping.Key.GroupId,
                grouping.Key.SubjectType,
                grouping.Key.SubjectId,
                Total = grouping.Count(),
                FirstExpiry = grouping.Min(item => item.approval.ExpiresAt)
            };
        // Every tie is broken explicitly, and runs without an id come last on both providers, so a
        // group, a subject and a run stay contiguous across pages.
        var ordered =
            from row in keyed
            join groupOf in groups
                on new { row.thread.ScopeKey, row.GroupType, row.GroupId }
                equals new { groupOf.ScopeKey, groupOf.GroupType, groupOf.GroupId }
            join subjectOf in subjects
                on new { row.thread.ScopeKey, row.GroupType, row.GroupId, row.thread.SubjectType, row.thread.SubjectId }
                equals new { subjectOf.ScopeKey, subjectOf.GroupType, subjectOf.GroupId, subjectOf.SubjectType, subjectOf.SubjectId }
            orderby groupOf.FirstExpiry,
                groupOf.ScopeKey,
                groupOf.GroupType,
                groupOf.GroupId,
                row.approval.ParentSubjectType == null ? 0 : 1,
                subjectOf.FirstExpiry,
                row.thread.SubjectType,
                row.thread.SubjectId,
                row.approval.RunId == null ? 1 : 0,
                row.approval.RunId,
                row.approval.ExpiresAt,
                row.approval.Id
            select new { row.approval, row.thread, GroupTotal = groupOf.Total, SubjectTotal = subjectOf.Total };

        // One proposal before and after the page tell whether its first group continues from the page
        // before and its last group on the page after.
        var offset = (page - 1) * itemsPerPage;
        var lead = offset > 0 ? 1 : 0;
        var window = await ordered
            .Skip(offset - lead)
            .Take(itemsPerPage + lead + 1)
            .ToListAsync(cancellationToken);
        var pageRows = window
            .Skip(lead)
            .Take(itemsPerPage)
            .Select(row => new NhAssistantGroupedInboxRow(new NhAssistantInboxRow(row.approval, row.thread), row.GroupTotal, row.SubjectTotal))
            .ToList();
        var previous = lead == 1 && window.Count > 0 ? new NhAssistantInboxRow(window[0].approval, window[0].thread) : null;
        var next = window.Count > lead + itemsPerPage
            ? new NhAssistantInboxRow(window[lead + itemsPerPage].approval, window[lead + itemsPerPage].thread)
            : null;
        return new NhAssistantInboxPage(pageRows.Select(row => row.Row).ToList(), total)
        {
            Groups = NhAssistantInboxGrouping.GroupsOf(pageRows, previous, next)
        };
    }
}

/// <summary>
/// Builds the groups of one page of the grouped inbox from its proposals, which the query returned
/// contiguous per group, subject and run.
/// </summary>
internal static class NhAssistantInboxGrouping
{
    public static IReadOnlyList<NhAssistantInboxGroupRow> GroupsOf(
        IReadOnlyList<NhAssistantGroupedInboxRow> rows,
        NhAssistantInboxRow? previous,
        NhAssistantInboxRow? next)
    {
        var groups = new List<NhAssistantInboxGroupRow>();
        var index = 0;
        while (index < rows.Count)
        {
            var key = GroupKeyOf(rows[index].Row);
            var end = index;
            while (end < rows.Count && GroupKeyOf(rows[end].Row) == key)
            {
                end++;
            }

            var members = rows.Skip(index).Take(end - index).ToList();
            groups.Add(new NhAssistantInboxGroupRow(
                key.ScopeKey,
                key.Type,
                key.Id,
                DisplayNameOf(members),
                members.Count,
                members[0].GroupTotal,
                index == 0 && previous is not null && GroupKeyOf(previous) == key,
                end == rows.Count && next is not null && GroupKeyOf(next) == key,
                SubjectsOf(members, index == 0 ? previous : null, end == rows.Count ? next : null)));
            index = end;
        }

        return groups;
    }

    private static IReadOnlyList<NhAssistantInboxSubjectRow> SubjectsOf(
        IReadOnlyList<NhAssistantGroupedInboxRow> rows,
        NhAssistantInboxRow? previous,
        NhAssistantInboxRow? next)
    {
        var subjects = new List<NhAssistantInboxSubjectRow>();
        var index = 0;
        while (index < rows.Count)
        {
            var key = SubjectKeyOf(rows[index].Row);
            var end = index;
            while (end < rows.Count && SubjectKeyOf(rows[end].Row) == key)
            {
                end++;
            }

            subjects.Add(new NhAssistantInboxSubjectRow(
                rows[index].Row.Thread.SubjectType,
                rows[index].Row.Thread.SubjectId,
                end - index,
                rows[index].SubjectTotal,
                index == 0 && previous is not null && SubjectKeyOf(previous) == key,
                end == rows.Count && next is not null && SubjectKeyOf(next) == key));
            index = end;
        }

        return subjects;
    }

    /// <summary>
    /// The group's name: its own subject's name when it has proposals in the group, otherwise the name
    /// the newest proposal recorded for the parent.
    /// </summary>
    private static string? DisplayNameOf(IReadOnlyList<NhAssistantGroupedInboxRow> rows)
    {
        var own = rows.FirstOrDefault(row => row.Row.Approval.ParentSubjectType is null);
        if (own is not null)
        {
            return own.Row.Thread.SubjectDisplayName;
        }

        return rows
            .Select(row => row.Row.Approval)
            .Where(approval => approval.ParentDisplayName is not null)
            .OrderByDescending(approval => approval.CreatedAt)
            .Select(approval => approval.ParentDisplayName)
            .FirstOrDefault();
    }

    private static (string ScopeKey, string Type, string Id) GroupKeyOf(NhAssistantInboxRow row)
    {
        return (
            row.Thread.ScopeKey,
            row.Approval.ParentSubjectType ?? row.Thread.SubjectType,
            row.Approval.ParentSubjectId ?? row.Thread.SubjectId);
    }

    private static (string ScopeKey, string Type, string Id, string SubjectType, string SubjectId) SubjectKeyOf(NhAssistantInboxRow row)
    {
        var group = GroupKeyOf(row);
        return (group.ScopeKey, group.Type, group.Id, row.Thread.SubjectType, row.Thread.SubjectId);
    }
}

/// <summary>
/// An agent thread of the whole application: its conversation, its subject id and its agent.
/// </summary>
internal sealed record NhAssistantSystemThread(Guid ConversationId, string SubjectId, string AgentId);

/// <summary>
/// The threads of the whole application whose pending proposals one person's inbox lists: those the
/// person decides as an approver of the whole application and passes the agent's view policy for,
/// and those the person may see as initiator because they pass that view policy.
/// </summary>
internal sealed record NhAssistantSystemInbox(
    IReadOnlyCollection<Guid> ApproverConversationIds,
    IReadOnlyCollection<Guid> ViewerConversationIds)
{
    public static NhAssistantSystemInbox None { get; } = new([], []);
}

/// <summary>
/// Fields an approval transition sets; <see langword="null"/> keeps the stored value.
/// </summary>
internal sealed record NhAssistantApprovalTransition
{
    public static NhAssistantApprovalTransition None { get; } = new();

    public string? DecidedByActorId { get; init; }
    public DateTimeOffset? DecidedAt { get; init; }
    public DateTimeOffset? ApprovalExpiresAt { get; init; }
    public string? Reason { get; init; }
    public Guid? ExecutionOperationId { get; init; }

    /// <summary>
    /// Forgets the execution operation, so the maintenance job queues the execution again.
    /// </summary>
    public bool ClearExecutionOperation { get; init; }
    public bool CountExecutionAttempt { get; init; }
    public DateTimeOffset? ExecutedAt { get; init; }

    /// <summary>
    /// Forgets when an earlier execution ended, as a new hand-off to the application starts one.
    /// </summary>
    public bool ClearExecutedAt { get; init; }
    public string? ExecutionResultCode { get; init; }

    /// <summary>
    /// Until when the application's report is due, set when the proposal is handed to the application.
    /// </summary>
    public DateTimeOffset? ExecutionReportDueAt { get; init; }
    public DateTimeOffset? ExpiresAt { get; init; }
    public string? PolicyJson { get; init; }
    public DateTimeOffset? EscalatedAt { get; init; }

    /// <summary>
    /// The next reminder; with <see cref="ResetReminders"/> it replaces the stored value even when
    /// <see langword="null"/>, and the count of sent reminders starts again.
    /// </summary>
    public DateTimeOffset? NextReminderAt { get; init; }

    public bool ResetReminders { get; init; }
    public string? StaleKind { get; init; }
    public string? StaleExplanationsJson { get; init; }
    public string? StaleByActorId { get; init; }
    public Guid? SupersededByApprovalId { get; init; }
}

internal sealed record NhAssistantInboxFilter(
    string? SubjectType,
    string? AgentId,
    Guid? DivisionId,
    Guid? RunId);

internal sealed record NhAssistantInboxRow(AssistantApproval Approval, AssistantThread Thread);

/// <summary>
/// One page of the inbox and how many proposals the inbox has in total. <see cref="Groups"/> is set
/// for a grouped inbox only.
/// </summary>
internal sealed record NhAssistantInboxPage(IReadOnlyList<NhAssistantInboxRow> Items, int Total)
{
    public IReadOnlyList<NhAssistantInboxGroupRow>? Groups { get; init; }
}

/// <summary>
/// A proposal of the grouped inbox with the totals of its group and of its subject in that group.
/// </summary>
internal sealed record NhAssistantGroupedInboxRow(NhAssistantInboxRow Row, int GroupTotal, int SubjectTotal);

/// <summary>
/// One group on a page of the grouped inbox: a parent, or a subject whose proposals record none, with
/// its proposals on the page (<see cref="Count"/>) and in the whole inbox (<see cref="Total"/>).
/// </summary>
internal sealed record NhAssistantInboxGroupRow(
    string ScopeKey,
    string SubjectType,
    string SubjectId,
    string? DisplayName,
    int Count,
    int Total,
    bool ContinuesFromPreviousPage,
    bool ContinuesOnNextPage,
    IReadOnlyList<NhAssistantInboxSubjectRow> Subjects);

/// <summary>
/// One subject within a group on a page of the grouped inbox.
/// </summary>
internal sealed record NhAssistantInboxSubjectRow(
    string SubjectType,
    string SubjectId,
    int Count,
    int Total,
    bool ContinuesFromPreviousPage,
    bool ContinuesOnNextPage);

/// <summary>
/// Which runs a run history lists: of an agent, on a subject type or one subject, in a status, created
/// from <see cref="From"/> (inclusive) until <see cref="To"/> (exclusive). Every part is optional.
/// </summary>
internal sealed record NhAssistantRunFilter(
    string? AgentId,
    string? SubjectType,
    string? SubjectId,
    string? Status,
    DateTimeOffset? From,
    DateTimeOffset? To);

internal sealed record NhAssistantRunRow(AssistantRun Run, AssistantThread Thread);

internal sealed record NhAssistantOpenProposalRow(AssistantApproval Approval, AssistantThread Thread);

/// <summary>
/// A consultation proposal the asker decides in the asking conversation, with its consultation and
/// the consulted agent's thread it waits in.
/// </summary>
internal sealed record NhAssistantAskerDecisionRow(AssistantApproval Approval, AssistantConsultation Consultation, AssistantThread Thread);

internal sealed class NhAssistantAskerDecisionQueryRow
{
    public required AssistantApproval Approval { get; init; }

    public required AssistantConsultation Consultation { get; init; }
}

internal sealed record NhAssistantSubjectSummaryRow(string SubjectId, int OpenProposals, int ProposalsToDecide, bool RunActive);
