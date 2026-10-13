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

    public async Task<AssistantRun?> FindQueuedRunAsync(Guid conversationId, CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        return await context.Runs
            .AsNoTracking()
            .Where(run => run.ConversationId == conversationId && run.Status == NhAssistantRunStatuses.Queued)
            .OrderBy(run => run.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);
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

    public async Task<IReadOnlyList<AssistantRun>> GetActiveRunsAsync(string agentId, CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        return await context.Runs
            .AsNoTracking()
            .Where(run => ActiveRunStatuses.Contains(run.Status))
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
        CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        await context.Runs
            .Where(run => run.Id == runId)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(run => run.ToolCalls, run => run.ToolCalls + toolCalls)
                    .SetProperty(run => run.InputTokens, run => run.InputTokens + inputTokens)
                    .SetProperty(run => run.OutputTokens, run => run.OutputTokens + outputTokens)
                    .SetProperty(run => run.ProposalCount, run => run.ProposalCount + proposals),
                cancellationToken);
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
        CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        var now = DateTimeOffset.UtcNow;
        await context.QueuedInputs
            .Where(input => input.ConversationId == conversationId && input.ConsumedAt == null)
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

    public async Task<int> CountWaitingInputsAsync(Guid conversationId, CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        return await context.QueuedInputs.CountAsync(
            input => input.ConversationId == conversationId && input.ConsumedAt == null,
            cancellationToken);
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
}

internal sealed record NhAssistantInboxFilter(
    string? SubjectType,
    string? AgentId,
    Guid? DivisionId,
    Guid? RunId);

internal sealed record NhAssistantInboxRow(AssistantApproval Approval, AssistantThread Thread);

internal sealed record NhAssistantSubjectSummaryRow(string SubjectId, int OpenProposals, int ProposalsToDecide, bool RunActive);
