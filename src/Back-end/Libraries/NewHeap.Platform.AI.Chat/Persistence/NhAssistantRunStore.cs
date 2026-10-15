using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NewHeap.Platform.AI.Chat.Entities;

namespace NewHeap.Platform.AI.Chat.Persistence;

/// <summary>
/// Persistence of agent threads, runs, queued inputs, subject member snapshots and the approval
/// queries of the inbox and the maintenance job. Every call uses its own short-lived context;
/// status transitions are compare-and-set updates, so concurrent workers never overwrite each other.
/// </summary>
internal sealed class NhAssistantRunStore(NhAssistantDbContextFactory contextFactory)
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

    public async Task<AssistantThread?> FindThreadAsync(
        string scopeKey,
        string agentId,
        NhAiSubjectRef subject,
        CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        return await context.Threads
            .AsNoTracking()
            .SingleOrDefaultAsync(
                thread => thread.ScopeKey == scopeKey
                    && thread.AgentId == agentId
                    && thread.SubjectType == subject.Type
                    && thread.SubjectId == subject.Id,
                cancellationToken);
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
                cancellationToken);
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
    /// rules or triage will route has none.
    /// </summary>
    public async Task<AssistantRun?> FindQueuedRunAsync(
        Guid conversationId,
        CancellationToken cancellationToken,
        string? skillId = null)
    {
        await using var context = contextFactory.CreateDbContext();
        return await context.Runs
            .AsNoTracking()
            .Where(run => run.ConversationId == conversationId
                && run.Status == NhAssistantRunStatuses.Queued
                && run.SkillId == skillId)
            .OrderBy(run => run.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);
    }

    /// <summary>
    /// Records the skill a run works in and who chose it.
    /// </summary>
    public async Task SetRunSkillAsync(Guid runId, string skillId, string chosenBy, CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        await context.Runs
            .Where(run => run.Id == runId)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(run => run.SkillId, skillId)
                    .SetProperty(run => run.SkillChosenBy, chosenBy),
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
        var since = DateTimeOffset.UtcNow.AddMinutes(-5);
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

    public async Task<AssistantRun?> FindActiveRunAsync(Guid conversationId, CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        return await context.Runs
            .AsNoTracking()
            .Where(run => run.ConversationId == conversationId && ActiveRunStatuses.Contains(run.Status))
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

    public async Task<int> CountRunsSinceAsync(
        Guid conversationId,
        DateTimeOffset since,
        CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        return await context.Runs.CountAsync(
            run => run.ConversationId == conversationId && run.CreatedAt >= since,
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
        string? skillId = null)
    {
        await using var context = contextFactory.CreateDbContext();
        var now = DateTimeOffset.UtcNow;
        // A run claims the inputs without a skill and those of its own skill.
        await context.QueuedInputs
            .Where(input => input.ConversationId == conversationId
                && input.ConsumedAt == null
                && (input.SkillId == null || input.SkillId == skillId))
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
    /// Gives the inputs a run claimed back to the thread as waiting inputs of
    /// <paramref name="skillId"/>, when the run's rules routed it to a paused skill.
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
        var now = DateTimeOffset.UtcNow;
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
                        approval => decision.ExecutedAt ?? approval.ExecutedAt)
                    .SetProperty(
                        approval => approval.ExecutionResultCode,
                        approval => decision.ExecutionResultCode ?? approval.ExecutionResultCode)
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
    /// Pending proposals of agent threads, oldest expiry first, for the maintenance job.
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
                && approval.RunId != null
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
    /// same identity, or <see langword="null"/>.
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
                && OpenProposalStatuses.Contains(approval.Status))
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
                && approval.DecidedAt >= since)
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
        NhAssistantApprovalStatuses.Executing
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
    /// may decide as the inbox counts them. Subjects without visible work are left out.
    /// </summary>
    public async Task<IReadOnlyList<NhAssistantSubjectSummaryRow>> SummarizeSubjectsAsync(
        string actorId,
        string subjectType,
        IReadOnlyCollection<string> subjectIds,
        IReadOnlyCollection<string> hiddenAgentIds,
        CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        var now = DateTimeOffset.UtcNow;
        var memberships = context.SubjectMembers
            .Where(member => member.ActorId == actorId
                && member.SubjectType == subjectType
                && subjectIds.Contains(member.SubjectId));
        var threads = context.Threads
            .AsNoTracking()
            .Where(thread => thread.SubjectType == subjectType
                && subjectIds.Contains(thread.SubjectId)
                && !hiddenAgentIds.Contains(thread.AgentId)
                && memberships.Any(member => member.ScopeKey == thread.ScopeKey && member.SubjectId == thread.SubjectId));
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
            .Where(row => row.approval.InitiatorActorId == actorId
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
    /// snapshot: as an approver of the subject, or as the initiator of the run. Each decision is
    /// checked live again.
    /// </summary>
    public async Task<(IReadOnlyList<NhAssistantInboxRow> Items, int Total)> ListInboxAsync(
        string actorId,
        NhAssistantInboxFilter filter,
        int page,
        int itemsPerPage,
        CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        var now = DateTimeOffset.UtcNow;
        var approverSubjects = context.SubjectMembers
            .Where(member => member.ActorId == actorId && member.Role >= NhAiSubjectRole.Approver);
        var query =
            from approval in context.Approvals.AsNoTracking()
            join thread in context.Threads.AsNoTracking() on approval.ConversationId equals thread.ConversationId
            where approval.Status == NhAssistantApprovalStatuses.Pending
                && approval.ExpiresAt > now
                && (approverSubjects.Any(member => member.ScopeKey == thread.ScopeKey
                        && member.SubjectType == thread.SubjectType
                        && member.SubjectId == thread.SubjectId)
                    || approval.InitiatorActorId == actorId)
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
        var rows = await query
            .OrderBy(row => row.approval.ExpiresAt)
            .ThenBy(row => row.approval.Id)
            .Skip((page - 1) * itemsPerPage)
            .Take(itemsPerPage)
            .ToListAsync(cancellationToken);
        return (rows.Select(row => new NhAssistantInboxRow(row.approval, row.thread)).ToList(), total);
    }
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
    public string? ExecutionResultCode { get; init; }
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

internal sealed record NhAssistantOpenProposalRow(AssistantApproval Approval, AssistantThread Thread);

internal sealed record NhAssistantSubjectSummaryRow(string SubjectId, int OpenProposals, int ProposalsToDecide, bool RunActive);
