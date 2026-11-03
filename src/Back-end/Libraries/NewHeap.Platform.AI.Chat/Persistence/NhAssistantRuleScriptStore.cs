using Microsoft.EntityFrameworkCore;
using NewHeap.Platform.AI.Chat.Entities;

namespace NewHeap.Platform.AI.Chat.Persistence;

/// <summary>
/// The rule scripts of agents and skills (RB-2): one row per agent and skill with the live version, the
/// candidate and a revision that every change expects, the append-only versions and the records of their
/// dry runs and shadow runs. Every read goes to the database, so a version set live applies to the next
/// run on every host.
/// </summary>
internal sealed class NhAssistantRuleScriptStore(NhAssistantDbContextFactory contextFactory)
{
    /// <summary>
    /// The rule script of the agent's own rules (<paramref name="skillId"/> empty) or of one skill, with
    /// every version; <see langword="null"/> when none was ever saved.
    /// </summary>
    public async Task<AssistantRuleScript?> FindAsync(string agentId, string skillId, CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        return await context.RuleScripts
            .AsNoTracking()
            .Include(item => item.Versions)
            .AsSplitQuery()
            .SingleOrDefaultAsync(item => item.AgentId == agentId && item.SkillId == skillId, cancellationToken);
    }

    /// <summary>
    /// The rule scripts of every skill of the agent and of its own rules, without the versions.
    /// </summary>
    public async Task<IReadOnlyList<AssistantRuleScript>> GetOfAgentAsync(string agentId, CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        return await context.RuleScripts
            .AsNoTracking()
            .Where(item => item.AgentId == agentId)
            .OrderBy(item => item.SkillId)
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// What a rule turn of the agent or skill runs: the live version, and the candidate when it runs in
    /// shadow; both <see langword="null"/> without them.
    /// </summary>
    public async Task<(AssistantRuleScriptVersion? Live, AssistantRuleScriptVersion? Shadow)> FindInEffectAsync(
        string agentId,
        string skillId,
        CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        var versions = await context.RuleScriptVersions
            .AsNoTracking()
            .Where(version => version.AgentId == agentId
                && version.SkillId == skillId
                && (version.Status == NhAssistantRuleScriptStatuses.Live
                    || (version.Status == NhAssistantRuleScriptStatuses.Candidate && version.RuleScript.CandidateShadow)))
            .ToListAsync(cancellationToken);
        return (
            versions.FirstOrDefault(version => version.Status == NhAssistantRuleScriptStatuses.Live),
            versions.FirstOrDefault(version => version.Status == NhAssistantRuleScriptStatuses.Candidate));
    }

    /// <summary>
    /// The agents and skills, as agent id and skill id, with a live version or a candidate in shadow,
    /// among <paramref name="agentIds"/>.
    /// </summary>
    public async Task<IReadOnlyList<(string AgentId, string SkillId)>> GetInEffectAsync(
        IReadOnlyCollection<string> agentIds,
        CancellationToken cancellationToken)
    {
        if (agentIds.Count == 0)
        {
            return [];
        }

        await using var context = contextFactory.CreateDbContext();
        var rows = await context.RuleScripts
            .AsNoTracking()
            .Where(item => agentIds.Contains(item.AgentId) && (item.LiveVersion != null || item.CandidateShadow))
            .Select(item => new { item.AgentId, item.SkillId })
            .ToListAsync(cancellationToken);
        return rows.Select(row => (row.AgentId, row.SkillId)).ToArray();
    }

    /// <summary>
    /// One version; <see langword="null"/> when it does not exist.
    /// </summary>
    public async Task<AssistantRuleScriptVersion?> FindVersionAsync(
        string agentId,
        string skillId,
        int version,
        CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        return await context.RuleScriptVersions
            .AsNoTracking()
            .SingleOrDefaultAsync(item => item.AgentId == agentId && item.SkillId == skillId && item.Version == version, cancellationToken);
    }

    /// <summary>
    /// Changes the rule script of the agent or skill when its revision is still
    /// <paramref name="expectedRevision"/> (0 before the first version): <paramref name="change"/> works on
    /// the tracked rule script, created when it does not exist, and its versions, and returns
    /// <see langword="null"/> to save or the failure that saves nothing. The revision goes up by one.
    /// </summary>
    public async Task<NhAssistantRuleScriptChange> ChangeAsync(
        string agentId,
        string skillId,
        int expectedRevision,
        Func<AssistantRuleScript, NhAssistantDbContext, Common.Models.TaskResult?> change,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(change);
        await using var context = contextFactory.CreateDbContext();
        var ruleScript = await context.RuleScripts
            .Include(item => item.Versions)
            .AsSplitQuery()
            .SingleOrDefaultAsync(item => item.AgentId == agentId && item.SkillId == skillId, cancellationToken);
        if ((ruleScript?.Revision ?? 0) != expectedRevision)
        {
            return NhAssistantRuleScriptChange.Conflict;
        }

        if (ruleScript is null)
        {
            ruleScript = new AssistantRuleScript
            {
                Id = Guid.NewGuid(),
                AgentId = agentId,
                SkillId = skillId
            };
            context.RuleScripts.Add(ruleScript);
        }

        var failure = change(ruleScript, context);
        if (failure is not null)
        {
            return NhAssistantRuleScriptChange.Failed(failure);
        }

        ruleScript.Revision = expectedRevision + 1;
        try
        {
            await context.SaveChangesAsync(cancellationToken);
            return NhAssistantRuleScriptChange.Changed(ruleScript);
        }
        catch (DbUpdateConcurrencyException)
        {
            return NhAssistantRuleScriptChange.Conflict;
        }
        catch (DbUpdateException) when (expectedRevision == 0)
        {
            // Two first versions at once: the unique rule script per agent and skill took the other one.
            // Any other failure, such as an agent that is gone, is no conflict.
            if (await ExistsAsync(agentId, skillId, cancellationToken))
            {
                return NhAssistantRuleScriptChange.Conflict;
            }

            throw;
        }
    }

    /// <summary>
    /// Adds <paramref name="version"/> in the transaction of an agent package's import when the rule
    /// script's revision is still <paramref name="expectedRevision"/>: live, superseding the live version,
    /// or as the candidate, discarding the one that waited. <see langword="false"/> on a conflict.
    /// </summary>
    public static async Task<bool> AddImportedVersionAsync(
        NhAssistantDbContext context,
        AssistantRuleScriptVersion version,
        int expectedRevision,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(version);
        var ruleScript = await context.RuleScripts
            .Include(item => item.Versions)
            .AsSplitQuery()
            .SingleOrDefaultAsync(item => item.AgentId == version.AgentId && item.SkillId == version.SkillId, cancellationToken);
        if ((ruleScript?.Revision ?? 0) != expectedRevision)
        {
            return false;
        }

        if (ruleScript is null)
        {
            ruleScript = new AssistantRuleScript
            {
                Id = Guid.NewGuid(),
                AgentId = version.AgentId,
                SkillId = version.SkillId
            };
            context.RuleScripts.Add(ruleScript);
        }

        var ended = version.Status == NhAssistantRuleScriptStatuses.Live
            ? ruleScript.Versions.Where(item => item.Status is NhAssistantRuleScriptStatuses.Live or NhAssistantRuleScriptStatuses.Candidate)
            : ruleScript.Versions.Where(item => item.Status == NhAssistantRuleScriptStatuses.Candidate);
        foreach (var item in ended)
        {
            item.Status = item.Status == NhAssistantRuleScriptStatuses.Live
                ? NhAssistantRuleScriptStatuses.Superseded
                : NhAssistantRuleScriptStatuses.Discarded;
            item.EndedAt = version.CreatedAt;
            item.EndedBy = version.AuthorActorId;
        }

        version.RuleScriptId = ruleScript.Id;
        version.Version = ruleScript.LatestVersion + 1;
        ruleScript.Versions.Add(version);
        ruleScript.LatestVersion = version.Version;
        ruleScript.CandidateVersion = version.Status == NhAssistantRuleScriptStatuses.Candidate ? version.Version : null;
        ruleScript.CandidateShadow = false;
        if (version.Status == NhAssistantRuleScriptStatuses.Live)
        {
            ruleScript.LiveVersion = version.Version;
        }

        ruleScript.Revision = expectedRevision + 1;
        ruleScript.ChangedAt = version.CreatedAt;
        ruleScript.ChangedBy = version.AuthorActorId;
        try
        {
            await context.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            return false;
        }
    }

    /// <summary>
    /// The live versions of the agent's rule scripts, of its own rules and of every skill.
    /// </summary>
    public async Task<IReadOnlyList<AssistantRuleScriptVersion>> GetLiveOfAgentAsync(string agentId, CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        return await context.RuleScriptVersions
            .AsNoTracking()
            .Where(item => item.AgentId == agentId && item.Status == NhAssistantRuleScriptStatuses.Live)
            .OrderBy(item => item.SkillId)
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Stores the record of a dry run or shadow run and keeps only the newest <paramref name="keep"/> of
    /// the version of that kind.
    /// </summary>
    public async Task AddRunRecordAsync(AssistantRuleScriptRunRecord record, int keep, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentOutOfRangeException.ThrowIfLessThan(keep, 1);
        await using var context = contextFactory.CreateDbContext();
        context.RuleScriptRunRecords.Add(record);
        await context.SaveChangesAsync(cancellationToken);

        // The record just stored is always kept, also when an older one has the same time.
        var older = await context.RuleScriptRunRecords
            .Where(item => item.VersionId == record.VersionId && item.Kind == record.Kind && item.Id != record.Id)
            .OrderByDescending(item => item.RanAt)
            .ThenByDescending(item => item.Id)
            .Skip(keep - 1)
            .Select(item => item.Id)
            .ToListAsync(cancellationToken);
        if (older.Count > 0)
        {
            await context.RuleScriptRunRecords
                .Where(item => older.Contains(item.Id))
                .ExecuteDeleteAsync(cancellationToken);
        }
    }

    /// <summary>
    /// The newest records of a version of one kind, newest first.
    /// </summary>
    public async Task<IReadOnlyList<AssistantRuleScriptRunRecord>> GetRunRecordsAsync(
        Guid versionId,
        string kind,
        int take,
        CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        return await context.RuleScriptRunRecords
            .AsNoTracking()
            .Where(item => item.VersionId == versionId && item.Kind == kind)
            .OrderByDescending(item => item.RanAt)
            .ThenByDescending(item => item.Id)
            .Take(take)
            .ToListAsync(cancellationToken);
    }

    private async Task<bool> ExistsAsync(string agentId, string skillId, CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        return await context.RuleScripts.AnyAsync(item => item.AgentId == agentId && item.SkillId == skillId, cancellationToken);
    }
}

/// <summary>
/// What became of a change of a rule script.
/// </summary>
internal sealed record NhAssistantRuleScriptChange
{
    private NhAssistantRuleScriptChange()
    {
    }

    /// <summary>The revision changed since it was read, or another change wrote first; nothing was saved.</summary>
    public static NhAssistantRuleScriptChange Conflict { get; } = new() { IsConflict = true };

    public bool IsConflict { get; private init; }

    /// <summary>The change refused itself; nothing was saved.</summary>
    public Common.Models.TaskResult? Failure { get; private init; }

    /// <summary>The rule script as saved.</summary>
    public AssistantRuleScript? RuleScript { get; private init; }

    public static NhAssistantRuleScriptChange Failed(Common.Models.TaskResult failure)
    {
        return new NhAssistantRuleScriptChange { Failure = failure };
    }

    public static NhAssistantRuleScriptChange Changed(AssistantRuleScript ruleScript)
    {
        return new NhAssistantRuleScriptChange { RuleScript = ruleScript };
    }
}
