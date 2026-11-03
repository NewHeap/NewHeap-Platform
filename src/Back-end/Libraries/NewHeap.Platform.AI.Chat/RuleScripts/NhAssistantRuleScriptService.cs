using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Chat.Governance;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.AI.Chat.Runs;
using NewHeap.Platform.AI.Chat.Runtime;
using NewHeap.Platform.Common.Models;

namespace NewHeap.Platform.AI.Chat.RuleScripts;

/// <summary>
/// The administration of rule scripts (<see cref="INhAssistantRuleScripts"/>): versions within the code's
/// ceiling, going live with optional four eyes, shadow, discarding, comparing and dry runs without effects.
/// Every change expects the rule script's revision and is audited content-free; every time follows the
/// application's clock.
/// </summary>
internal sealed class NhAssistantRuleScriptService(
    NhAssistantAgentCatalog agents,
    NhAssistantRegistrationState registration,
    NhAssistantRuleScriptStore store,
    NhAssistantAgentEvents events,
    IServiceProvider services,
    ILogger<NhAssistantRuleScriptService> logger,
    TimeProvider? timeProvider = null) : INhAssistantRuleScripts
{
    private const int MaxActorIdLength = 256;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly TimeProvider _applicationClock = timeProvider ?? TimeProvider.System;

    public async Task<NhAssistantRuleScriptDto?> GetAsync(string agentId, string? skillId, CancellationToken cancellationToken = default)
    {
        var target = await ResolveAsync(agentId, skillId, cancellationToken);
        if (!target.Success)
        {
            return null;
        }

        var (agent, options) = target.Data;
        var ruleScript = await store.FindAsync(agentId, KeyOf(skillId), cancellationToken);
        var ceiling = NhAssistantRuleScriptRuntime.CeilingOf(registration, agentId, agent.Source);
        var versions = (ruleScript?.Versions ?? [])
            .OrderByDescending(version => version.Version)
            .Select(version => ToDto(version, ruleScript!))
            .ToArray();
        return new NhAssistantRuleScriptDto(
            agentId,
            skillId,
            ceiling is not null,
            ruleScript?.Revision ?? 0,
            ruleScript?.LiveVersion,
            ruleScript?.CandidateVersion,
            ruleScript?.CandidateShadow ?? false,
            CodeRuleOf(options, skillId),
            CeilingDto(ceiling),
            versions);
    }

    public async Task<TaskResult<NhAssistantRuleScriptCatalogDto>> GetCatalogAsync(
        string agentId,
        string? skillId,
        IReadOnlyCollection<string>? writes = null,
        CancellationToken cancellationToken = default)
    {
        var target = await ResolveAsync(agentId, skillId, cancellationToken);
        if (!target.Success)
        {
            return TaskResult<NhAssistantRuleScriptCatalogDto>.Failed(target);
        }

        var (agent, options) = target.Data;
        var ceiling = NhAssistantRuleScriptRuntime.CeilingOf(registration, agentId, agent.Source);
        if (ceiling is null || services.GetService<INhAssistantRuleScriptEngine>() is not { } engine)
        {
            return TaskResult<NhAssistantRuleScriptCatalogDto>.Failed(NotAllowed());
        }

        var asked = (writes ?? ceiling.Writes).Where(ceiling.Writes.Contains).ToHashSet(StringComparer.Ordinal);
        var tools = CatalogToolsOf(agent.Definition, options, skillId);
        var declarations = engine.Describe(agentId, tools, asked);
        var effects = tools.ToDictionary(tool => tool.Id, tool => tool.Effect, StringComparer.Ordinal);
        return TaskResult<NhAssistantRuleScriptCatalogDto>.Succeeded(new NhAssistantRuleScriptCatalogDto(
            agentId,
            skillId,
            declarations.Tools
                .Select(tool =>
                {
                    var destructive = effects.GetValueOrDefault(tool.ToolId) == NhAiToolEffect.Destructive;
                    var refusal = tool.Refusal ?? (destructive && !ceiling.Destructive.Contains(tool.ToolId)
                        ? "it is destructive and the code's ceiling does not let it run in a script."
                        : null);
                    return new NhAssistantRuleScriptToolDto(
                        tool.Name,
                        tool.ToolId,
                        tool.Write,
                        tool.Write && ceiling.Writes.Contains(tool.ToolId),
                        refusal is null,
                        destructive,
                        tool.Signature,
                        refusal);
                })
                .ToArray(),
            options.AlertKinds.ToArray(),
            declarations.PlatformFunctions,
            declarations.MaxToolCalls,
            declarations.TimeoutSeconds,
            Math.Min(declarations.MaxScriptLength, NhAssistantRuleScriptLimits.MaxScriptLength),
            ceiling.MaxWritesPerRun));
    }

    public async Task<TaskResult<NhAssistantRuleScriptCheckDto>> CheckAsync(
        string agentId,
        string? skillId,
        string script,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(script);
        var target = await ResolveAsync(agentId, skillId, cancellationToken);
        if (!target.Success)
        {
            return TaskResult<NhAssistantRuleScriptCheckDto>.Failed(target);
        }
        if (NhAssistantRuleScriptRuntime.CeilingOf(registration, agentId, target.Data.Agent.Source) is null
            || services.GetService<INhAssistantRuleScriptEngine>() is not { } engine)
        {
            return TaskResult<NhAssistantRuleScriptCheckDto>.Failed(NotAllowed());
        }

        var problem = script.Length > NhAssistantRuleScriptLimits.MaxScriptLength
            ? new NhAssistantRuleScriptProblem("ai-script-too-long", "The script is longer than a version stores.")
            : engine.Check(agentId, script);
        return TaskResult<NhAssistantRuleScriptCheckDto>.Succeeded(problem is null
            ? new NhAssistantRuleScriptCheckDto(true, null, null, null, null)
            : new NhAssistantRuleScriptCheckDto(false, problem.Code, problem.Message, problem.Line, problem.Column));
    }

    public async Task<TaskResult<NhAssistantRuleScriptVersionDto>> SaveAsync(
        string agentId,
        string? skillId,
        NhAssistantRuleScriptSaveRequest request,
        NhAssistantRuleScriptAuthor author,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateAuthor(author);
        var target = await ResolveAsync(agentId, skillId, cancellationToken);
        if (!target.Success)
        {
            return TaskResult<NhAssistantRuleScriptVersionDto>.Failed(target);
        }

        var ceiling = NhAssistantRuleScriptRuntime.CeilingOf(registration, agentId, target.Data.Agent.Source);
        if (ceiling is null || services.GetService<INhAssistantRuleScriptEngine>() is not { } engine)
        {
            return TaskResult<NhAssistantRuleScriptVersionDto>.Failed(NotAllowed());
        }

        var errors = new NhAssistantValidationErrors();
        var script = request.Script ?? string.Empty;
        if (string.IsNullOrWhiteSpace(script))
        {
            errors.Add("script", NhAssistantFieldErrors.Required);
        }
        else if (script.Length > NhAssistantRuleScriptLimits.MaxScriptLength)
        {
            errors.Add("script", NhAssistantFieldErrors.TooLong);
        }
        else if (engine.Check(agentId, script) is { } problem)
        {
            errors.Add("script", problem.Code == "ai-script-too-long" ? NhAssistantFieldErrors.TooLong : NhAssistantFieldErrors.Invalid);
        }

        var reason = Trimmed(request.Reason);
        errors.Require(reason is null || reason.Length <= NhAssistantRuleScriptLimits.MaxReasonLength, "reason", NhAssistantFieldErrors.TooLong);
        if (!errors.IsEmpty)
        {
            return TaskResult<NhAssistantRuleScriptVersionDto>.Failed(errors.ToResult());
        }

        var writes = (request.Writes ?? [])
            .Select(write => write?.Trim() ?? string.Empty)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var writeErrors = new NhAssistantValidationErrors();
        writeErrors.Require(writes.Length <= NhAssistantRuleScriptLimits.MaxWritesPerVersion, "writes", NhAssistantFieldErrors.TooLong);
        foreach (var write in writes)
        {
            writeErrors.Require(ceiling.Writes.Contains(write), "writes", NhAssistantFieldErrors.NotAllowed);
        }
        if (!writeErrors.IsEmpty)
        {
            return TaskResult<NhAssistantRuleScriptVersionDto>.Failed(writeErrors.ToResult(
                NhAssistantRuleScriptCodes.WriteNotAllowed,
                "A write is not one the code's ceiling allows for the agent's rule scripts."));
        }
        if (request.Live && registration.RuleScriptsAsData!.RequireFourEyes)
        {
            return TaskResult<NhAssistantRuleScriptVersionDto>.Failed(FourEyes());
        }
        if (await LockedAsync(agentId, skillId, NhAssistantAgentCatalog.ComputeHash(script), JsonSerializer.Serialize(writes, Json), cancellationToken) is { } locked)
        {
            return TaskResult<NhAssistantRuleScriptVersionDto>.Failed(locked);
        }

        var now = _applicationClock.GetUtcNow();
        AssistantRuleScriptVersion? saved = null;
        var changed = await store.ChangeAsync(agentId, KeyOf(skillId), request.ExpectedRevision ?? 0, (ruleScript, _) =>
        {
            saved = new AssistantRuleScriptVersion
            {
                Id = Guid.NewGuid(),
                RuleScriptId = ruleScript.Id,
                AgentId = agentId,
                SkillId = KeyOf(skillId),
                Version = ruleScript.LatestVersion + 1,
                Status = request.Live ? NhAssistantRuleScriptStatuses.Live : NhAssistantRuleScriptStatuses.Candidate,
                Script = script,
                ScriptHash = NhAssistantAgentCatalog.ComputeHash(script),
                WritesJson = JsonSerializer.Serialize(writes, Json),
                AuthorActorId = author.ActorId,
                AuthorAgentId = author.AgentId,
                Reason = reason,
                CreatedAt = now
            };
            // A newer version replaces the candidate that waited.
            End(ruleScript, ruleScript.CandidateVersion, NhAssistantRuleScriptStatuses.Discarded, author.ActorId, now);
            ruleScript.CandidateVersion = null;
            ruleScript.CandidateShadow = false;
            if (request.Live)
            {
                End(ruleScript, ruleScript.LiveVersion, NhAssistantRuleScriptStatuses.Superseded, author.ActorId, now);
                saved.LiveAt = now;
                saved.LiveBy = author.ActorId;
                ruleScript.LiveVersion = saved.Version;
            }
            else
            {
                ruleScript.CandidateVersion = saved.Version;
                ruleScript.CandidateShadow = request.Shadow;
            }

            ruleScript.LatestVersion = saved.Version;
            ruleScript.ChangedAt = now;
            ruleScript.ChangedBy = author.ActorId;
            ruleScript.Versions.Add(saved);
            return null;
        }, cancellationToken);
        if (Failure<NhAssistantRuleScriptVersionDto>(changed) is { } failure)
        {
            return failure;
        }

        await AuditAsync(NhAssistantAuditEventKind.AdminRuleScriptSaved, saved!, author, saved!.Status, cancellationToken);
        if (request.Live)
        {
            await AuditAsync(NhAssistantAuditEventKind.AdminRuleScriptLive, saved, author, NhAssistantRuleScriptStatuses.Live, cancellationToken);
        }
        else if (request.Shadow)
        {
            await AuditAsync(NhAssistantAuditEventKind.AdminRuleScriptShadowChanged, saved, author, "shadow", cancellationToken);
        }

        return TaskResult<NhAssistantRuleScriptVersionDto>.Succeeded(ToDto(saved, changed.RuleScript!));
    }

    public async Task<TaskResult<NhAssistantRuleScriptLiveDto>> SetLiveAsync(
        string agentId,
        string? skillId,
        int version,
        NhAssistantRuleScriptChangeRequest request,
        NhAssistantRuleScriptAuthor actor,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateAuthor(actor);
        var prepared = await PrepareChangeAsync(agentId, skillId, request.Reason, cancellationToken);
        if (!prepared.Success)
        {
            return TaskResult<NhAssistantRuleScriptLiveDto>.Failed(prepared);
        }

        if (await store.FindVersionAsync(agentId, KeyOf(skillId), version, cancellationToken) is { } going
            && await LockedAsync(agentId, skillId, going.ScriptHash, going.WritesJson, cancellationToken) is { } locked)
        {
            return TaskResult<NhAssistantRuleScriptLiveDto>.Failed(locked);
        }

        var now = _applicationClock.GetUtcNow();
        AssistantRuleScriptVersion? live = null;
        var changed = await store.ChangeAsync(agentId, KeyOf(skillId), request.ExpectedRevision ?? 0, (ruleScript, _) =>
        {
            if (VersionOf(ruleScript, version) is not { } candidate)
            {
                return VersionNotFound();
            }
            if (candidate.Status != NhAssistantRuleScriptStatuses.Candidate)
            {
                return NotCandidate();
            }
            if (registration.RuleScriptsAsData!.RequireFourEyes
                && string.Equals(candidate.AuthorActorId, actor.ActorId, StringComparison.Ordinal))
            {
                return FourEyes();
            }

            End(ruleScript, ruleScript.LiveVersion, NhAssistantRuleScriptStatuses.Superseded, actor.ActorId, now);
            candidate.Status = NhAssistantRuleScriptStatuses.Live;
            candidate.LiveAt = now;
            candidate.LiveBy = actor.ActorId;
            ruleScript.LiveVersion = candidate.Version;
            ruleScript.CandidateVersion = null;
            ruleScript.CandidateShadow = false;
            ruleScript.ChangedAt = now;
            ruleScript.ChangedBy = actor.ActorId;
            live = candidate;
            return null;
        }, cancellationToken);
        if (Failure<NhAssistantRuleScriptLiveDto>(changed) is { } failure)
        {
            return failure;
        }

        await AuditAsync(NhAssistantAuditEventKind.AdminRuleScriptLive, live!, actor, NhAssistantRuleScriptStatuses.Live, cancellationToken);
        var dryRun = (await store.GetRunRecordsAsync(live!.Id, NhAssistantRuleScriptRunKinds.DryRun, 1, cancellationToken)).FirstOrDefault();
        return TaskResult<NhAssistantRuleScriptLiveDto>.Succeeded(new NhAssistantRuleScriptLiveDto(
            ToDto(live, changed.RuleScript!),
            dryRun is null ? null : NhAssistantRuleScriptRuntime.ToDto(dryRun)));
    }

    public async Task<TaskResult<NhAssistantRuleScriptVersionDto>> SetShadowAsync(
        string agentId,
        string? skillId,
        int version,
        NhAssistantRuleScriptShadowRequest request,
        NhAssistantRuleScriptAuthor actor,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateAuthor(actor);
        var prepared = await PrepareChangeAsync(agentId, skillId, request.Reason, cancellationToken);
        if (!prepared.Success)
        {
            return TaskResult<NhAssistantRuleScriptVersionDto>.Failed(prepared);
        }

        var now = _applicationClock.GetUtcNow();
        AssistantRuleScriptVersion? candidate = null;
        var changed = await store.ChangeAsync(agentId, KeyOf(skillId), request.ExpectedRevision ?? 0, (ruleScript, _) =>
        {
            candidate = VersionOf(ruleScript, version);
            if (candidate is null)
            {
                return VersionNotFound();
            }
            if (candidate.Status != NhAssistantRuleScriptStatuses.Candidate)
            {
                return NotCandidate();
            }

            ruleScript.CandidateShadow = request.Shadow;
            ruleScript.ChangedAt = now;
            ruleScript.ChangedBy = actor.ActorId;
            return null;
        }, cancellationToken);
        if (Failure<NhAssistantRuleScriptVersionDto>(changed) is { } failure)
        {
            return failure;
        }

        await AuditAsync(NhAssistantAuditEventKind.AdminRuleScriptShadowChanged, candidate!, actor, request.Shadow ? "shadow" : "no-shadow", cancellationToken);
        return TaskResult<NhAssistantRuleScriptVersionDto>.Succeeded(ToDto(candidate!, changed.RuleScript!));
    }

    public async Task<TaskResult<NhAssistantRuleScriptVersionDto>> DiscardAsync(
        string agentId,
        string? skillId,
        int version,
        NhAssistantRuleScriptChangeRequest request,
        NhAssistantRuleScriptAuthor actor,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateAuthor(actor);
        var prepared = await PrepareChangeAsync(agentId, skillId, request.Reason, cancellationToken);
        if (!prepared.Success)
        {
            return TaskResult<NhAssistantRuleScriptVersionDto>.Failed(prepared);
        }

        var now = _applicationClock.GetUtcNow();
        AssistantRuleScriptVersion? discarded = null;
        var changed = await store.ChangeAsync(agentId, KeyOf(skillId), request.ExpectedRevision ?? 0, (ruleScript, _) =>
        {
            discarded = VersionOf(ruleScript, version);
            if (discarded is null)
            {
                return VersionNotFound();
            }
            if (discarded.Status != NhAssistantRuleScriptStatuses.Candidate)
            {
                return NotCandidate();
            }

            End(ruleScript, discarded.Version, NhAssistantRuleScriptStatuses.Discarded, actor.ActorId, now);
            ruleScript.CandidateVersion = null;
            ruleScript.CandidateShadow = false;
            ruleScript.ChangedAt = now;
            ruleScript.ChangedBy = actor.ActorId;
            return null;
        }, cancellationToken);
        if (Failure<NhAssistantRuleScriptVersionDto>(changed) is { } failure)
        {
            return failure;
        }

        await AuditAsync(NhAssistantAuditEventKind.AdminRuleScriptDiscarded, discarded!, actor, NhAssistantRuleScriptStatuses.Discarded, cancellationToken);
        return TaskResult<NhAssistantRuleScriptVersionDto>.Succeeded(ToDto(discarded!, changed.RuleScript!));
    }

    public async Task<NhAssistantRuleScriptRunRecordDto?> GetDryRunAsync(
        string agentId,
        string? skillId,
        int version,
        CancellationToken cancellationToken = default)
    {
        var found = await store.FindVersionAsync(agentId, KeyOf(skillId), version, cancellationToken);
        if (found is null)
        {
            return null;
        }

        var record = (await store.GetRunRecordsAsync(found.Id, NhAssistantRuleScriptRunKinds.DryRun, 1, cancellationToken)).FirstOrDefault();
        return record is null ? null : NhAssistantRuleScriptRuntime.ToDto(record);
    }

    public async Task<IReadOnlyList<NhAssistantRuleScriptRunRecordDto>> GetShadowRunsAsync(
        string agentId,
        string? skillId,
        int version,
        CancellationToken cancellationToken = default)
    {
        var found = await store.FindVersionAsync(agentId, KeyOf(skillId), version, cancellationToken);
        if (found is null)
        {
            return [];
        }

        return (await store.GetRunRecordsAsync(found.Id, NhAssistantRuleScriptRunKinds.Shadow, NhAssistantRuleScriptLimits.MaxShadowRunsKept, cancellationToken))
            .Select(NhAssistantRuleScriptRuntime.ToDto)
            .ToArray();
    }

    public async Task<TaskResult<NhAssistantRuleScriptComparisonDto>> CompareAsync(
        string agentId,
        string? skillId,
        int version,
        int? otherVersion,
        CancellationToken cancellationToken = default)
    {
        var target = await ResolveAsync(agentId, skillId, cancellationToken);
        if (!target.Success)
        {
            return TaskResult<NhAssistantRuleScriptComparisonDto>.Failed(target);
        }

        var ruleScript = await store.FindAsync(agentId, KeyOf(skillId), cancellationToken);
        if (ruleScript is null || VersionOf(ruleScript, version) is not { } compared)
        {
            return TaskResult<NhAssistantRuleScriptComparisonDto>.Failed(VersionNotFound());
        }

        // By default the live version, or, for the live version itself, the one before it.
        var otherNumber = otherVersion
            ?? (ruleScript.LiveVersion is { } liveVersion && liveVersion != version
                ? liveVersion
                : ruleScript.Versions.Where(item => item.Version < version).Select(item => (int?)item.Version).Max());
        var other = otherNumber is { } number ? VersionOf(ruleScript, number) : null;
        if (otherVersion is not null && other is null)
        {
            return TaskResult<NhAssistantRuleScriptComparisonDto>.Failed(VersionNotFound());
        }

        var writes = NhAssistantRuleScriptRuntime.WritesOf(compared);
        var otherWrites = other is null ? [] : NhAssistantRuleScriptRuntime.WritesOf(other);
        var dryRun = (await store.GetRunRecordsAsync(compared.Id, NhAssistantRuleScriptRunKinds.DryRun, 1, cancellationToken)).FirstOrDefault();
        var otherDryRun = other is null
            ? null
            : (await store.GetRunRecordsAsync(other.Id, NhAssistantRuleScriptRunKinds.DryRun, 1, cancellationToken)).FirstOrDefault();
        return TaskResult<NhAssistantRuleScriptComparisonDto>.Succeeded(new NhAssistantRuleScriptComparisonDto(
            compared.Version,
            other?.Version ?? 0,
            NhAssistantRuleScriptLines.Compare(other?.Script ?? string.Empty, compared.Script),
            writes.Except(otherWrites, StringComparer.Ordinal).ToArray(),
            otherWrites.Except(writes, StringComparer.Ordinal).ToArray(),
            dryRun is null ? null : NhAssistantRuleScriptRuntime.ToDto(dryRun),
            otherDryRun is null ? null : NhAssistantRuleScriptRuntime.ToDto(otherDryRun)));
    }

    public async Task<TaskResult<NhAssistantRuleScriptRunRecordDto>> DryRunAsync(
        string agentId,
        string? skillId,
        int version,
        NhAssistantRuleScriptDryRunRequest request,
        NhAssistantRuleScriptAuthor actor,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateAuthor(actor);
        var target = await ResolveAsync(agentId, skillId, cancellationToken);
        if (!target.Success)
        {
            return TaskResult<NhAssistantRuleScriptRunRecordDto>.Failed(target);
        }

        var (agent, options) = target.Data;
        var ceiling = NhAssistantRuleScriptRuntime.CeilingOf(registration, agentId, agent.Source);
        if (ceiling is null || services.GetService<INhAssistantRuleScriptEngine>() is not { } engine)
        {
            return TaskResult<NhAssistantRuleScriptRunRecordDto>.Failed(NotAllowed());
        }

        var subjects = SubjectsOf(request.Subjects);
        if (subjects is null)
        {
            return TaskResult<NhAssistantRuleScriptRunRecordDto>.Failed(new NhAssistantValidationErrors()
                .Add("subjects", NhAssistantFieldErrors.Invalid)
                .ToResult(NhAssistantRuleScriptCodes.SubjectsInvalid, "A dry run names one to twenty valid subjects."));
        }

        var found = await store.FindVersionAsync(agentId, KeyOf(skillId), version, cancellationToken);
        if (found is null)
        {
            return TaskResult<NhAssistantRuleScriptRunRecordDto>.Failed(VersionNotFound());
        }

        var dryRunId = Guid.NewGuid();
        var records = new List<NhAssistantRuleScriptSubjectRunDto>(subjects.Count);
        foreach (var subject in subjects)
        {
            records.Add(await DryRunOnAsync(engine, agent, options, skillId, found, ceiling, subject, dryRunId, cancellationToken));
        }

        var completed = records.Count(record => record.Status == "completed");
        var stored = new AssistantRuleScriptRunRecord
        {
            Id = dryRunId,
            VersionId = found.Id,
            AgentId = agentId,
            SkillId = KeyOf(skillId),
            Version = found.Version,
            Kind = NhAssistantRuleScriptRunKinds.DryRun,
            RanBy = actor.ActorId,
            RanAt = _applicationClock.GetUtcNow(),
            SubjectCount = records.Count,
            CompletedCount = completed,
            RecordJson = NhAssistantRuleScriptRuntime.Serialize(records)
        };
        // The latest dry run of a version is kept; the one before goes.
        await store.AddRunRecordAsync(stored, 1, cancellationToken);
        await AuditAsync(
            NhAssistantAuditEventKind.AdminRuleScriptDryRun,
            found,
            actor,
            completed == records.Count ? "completed" : "stopped",
            cancellationToken);
        return TaskResult<NhAssistantRuleScriptRunRecordDto>.Succeeded(NhAssistantRuleScriptRuntime.ToDto(stored));
    }

    /// <summary>
    /// Runs the version on one subject without effects, in the scope a headless run of the agent has on it:
    /// the agent's grants, its accountability and the rights its accountable account lends, never the
    /// administrator's; nothing it writes, proposes or raises happens.
    /// </summary>
    private async Task<NhAssistantRuleScriptSubjectRunDto> DryRunOnAsync(
        INhAssistantRuleScriptEngine engine,
        NhAssistantAgent agent,
        NhAssistantAgentRunOptions options,
        string? skillId,
        AssistantRuleScriptVersion version,
        NhAssistantRuleScriptAgentCeiling ceiling,
        NhAiSubjectRef subjectRef,
        Guid dryRunId,
        CancellationToken cancellationToken)
    {
        var info = await services.GetRequiredService<NhAssistantSubjectAccessService>().DescribeAsync(subjectRef, cancellationToken);
        if (info is null)
        {
            return NhAssistantRuleScriptRuntime.FailedRecord(subjectRef, NhAssistantErrorCodes.SubjectNotFound, "The subject was not found.");
        }

        var accountable = services.GetRequiredService<NhAssistantThreadService>().ResolveAccountableOwner(agent, info);
        if (!accountable.Success)
        {
            return NhAssistantRuleScriptRuntime.FailedRecord(
                subjectRef,
                NhAssistantResults.CodeOf(accountable, NhAssistantErrorCodes.AgentNotFound),
                "Nobody answers for the agent's runs on the subject.");
        }

        var definition = DefinitionOf(agent.Definition, options, skillId);
        var deadline = DateTimeOffset.UtcNow.Add(registration.Limits.TurnTimeout);
        var turn = new NhAssistantTurnScope
        {
            ConversationId = Guid.Empty,
            TurnId = dryRunId,
            Agent = definition,
            OwnerActorId = definition.ActorId,
            AccountableOwnerId = accountable.Data!,
            TenantId = info.TenantId,
            ModelProfileName = definition.ProfileName,
            PromptVersion = string.Empty,
            PromptHash = string.Empty,
            Deadline = deadline,
            Limits = registration.Limits,
            ShadowRun = true
        };
        var scope = new NhAiAgentInvocationScope(
            agent.Id,
            definition.ActorId,
            definition.Version,
            info,
            accountable.Data!,
            dryRunId.ToString(),
            NhAssistantInvocationGate.Purpose)
        {
            Accountability = options.Accountability,
            DelegateFromInitiator = false,
            IsRequestTurn = false,
            IsAgentWork = true,
            Grants = options.Grants,
            ElevatedReads = options.ElevatedReads,
            // Nothing changes in a dry run; the host never lets a write reach its tool either.
            RequireApprovalForEffects = true,
            AllowGatewayReads = options.AllowGatewayReads && options.Accountability != NhAiAgentAccountability.Agent,
            ActWithAccountableRights = options.ActWithAccountableRights && options.Accountability != NhAiAgentAccountability.Agent,
            CorrelationId = dryRunId.ToString("N"),
            Deadline = deadline,
            ModelProfileName = definition.ProfileName
        };

        using (NhAssistantExecutionScope.EnterTurn(turn))
        using (NhAiAgentInvocationScopes.Begin(scope))
        {
            var context = new NhAiInvocationContext(definition.ActorId, NhAssistantInvocationGate.Purpose, new Dictionary<string, string>(StringComparer.Ordinal))
            {
                ActorKind = NhAiActorKind.Agent,
                IsAgentWork = true,
                AccountableOwnerId = accountable.Data,
                TenantId = info.TenantId,
                CorrelationId = dryRunId.ToString("N")
            };
            var functions = await FunctionsOfAsync(definition, context, cancellationToken);
            var recording = new NhAssistantRuleScriptRecording();
            var writes = NhAssistantRuleScriptRuntime.WritesInEffect(version, ceiling);
            var scriptTools = NhAssistantRuleScriptRuntime.ToolsOf(functions);
            var origin = new NhAiToolCallOrigin(NhAiToolCallOriginKind.Script, NhAssistantRuleScriptTool.ToolId, version.ScriptHash)
            {
                ParentInvocationId = dryRunId,
                RuleScriptVersion = version.Version
            };
            var request = new NhAssistantToolStepRequest(NhAssistantRuleScriptTool.Descriptor, NhAiToolCallOriginKind.Script)
            {
                Hash = version.ScriptHash,
                MaxSteps = NhAssistantToolStepLimits.MaxSteps,
                MaxMutationSteps = writes.Count == 0 ? 0 : Math.Min(ceiling.MaxWritesPerRun, NhAssistantToolStepLimits.MaxSteps)
            };
            var callable = scriptTools.Where(tool => tool.Effect == NhAiToolEffect.ReadOnly || writes.Contains(tool.Id)).ToArray();
            var session = new NhAssistantToolStepSession(
                request,
                origin,
                callable,
                new NhAssistantRuleScriptDryHost(functions, recording, definition, options.Accountability, false, services, logger));
            var run = new NhAssistantRuleScriptRun((tool, input, rationale, _) =>
            {
                var toolId = functions.ContainsKey(tool)
                    ? tool
                    : functions.Values.OfType<INhAiGovernedAIFunction>().FirstOrDefault(function => function.Descriptor.ExportName == tool)?.Descriptor.Id;
                recording.AddProposal(tool, toolId, input, rationale);
                return Task.FromResult(NhAssistantRuleProposal.Refused(NhAssistantRuleScriptCodes.DryRunWrite));
            })
            {
                AgentId = agent.Id,
                SkillId = skillId,
                Version = version.Version,
                Script = version.Script,
                Mode = NhAssistantRuleScriptMode.DryRun,
                Subject = info,
                TriggerId = NhAssistantRuleScriptRuntime.DryRunTriggerId,
                Now = _applicationClock.GetUtcNow(),
                AlertKinds = options.AlertKinds,
                Tools = scriptTools,
                Writes = writes,
                Destructive = ceiling.Destructive.Where(writes.Contains).ToHashSet(StringComparer.Ordinal),
                MaxWrites = writes.Count == 0 ? 0 : ceiling.MaxWritesPerRun,
                Steps = session
            };

            var outcome = await engine.RunAsync(run, cancellationToken);
            return NhAssistantRuleScriptRuntime.RecordOf(info, outcome, recording);
        }
    }

    /// <summary>
    /// The governed functions a headless run of the agent's rules may use in the current agent scope: the
    /// tools it may discover, selected by its selectors, with effects from propose autonomy on, capped as
    /// in its rule turns.
    /// </summary>
    private async Task<IReadOnlyDictionary<string, AIFunction>> FunctionsOfAsync(
        NhAssistantAgentDefinition definition,
        NhAiInvocationContext context,
        CancellationToken cancellationToken)
    {
        var discovered = await services.GetRequiredService<INhAiToolDiscoveryService>().DiscoverAsync(
            new NhAiToolDiscoveryRequest(context, NhAiToolExposure.Agent),
            cancellationToken);
        var selected = discovered
            .Where(descriptor => definition.ToolSelectors.Any(selector => NhAssistantTurnRunner.IsSelected(selector, descriptor.Id)))
            .Where(descriptor => descriptor.Effect == NhAiToolEffect.ReadOnly || NhAssistantTurnRunner.OffersEffectsToRules(definition))
            .Where(descriptor => descriptor.CompositionFeature == NhAiCompositionFeature.None)
            .Select(descriptor => descriptor.Id)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .Take(registration.Limits.MaxToolsPerAgent)
            .ToHashSet(StringComparer.Ordinal);
        var functions = new Dictionary<string, AIFunction>(StringComparer.Ordinal);
        foreach (var catalog in services.GetServices<INhAiToolCatalog>().Select(NhAiToolCatalogViews.Of))
        {
            if (!catalog.Descriptors.Any(descriptor => selected.Contains(descriptor.Id)))
            {
                continue;
            }

            foreach (var function in catalog.CreateFunctions(services))
            {
                if (function is INhAiGovernedAIFunction governed && selected.Contains(governed.Descriptor.Id))
                {
                    functions.TryAdd(governed.Descriptor.Id, function);
                }
            }
        }

        return functions;
    }

    /// <summary>
    /// The registered tools a script of the agent or skill may name, as its selectors and autonomy select
    /// them, without asking whether a run may call each one: that follows the run's grants.
    /// </summary>
    private IReadOnlyList<NhAiToolDescriptor> CatalogToolsOf(
        NhAssistantAgentDefinition agent,
        NhAssistantAgentRunOptions options,
        string? skillId)
    {
        var definition = DefinitionOf(agent, options, skillId);
        var descriptors = services.GetServices<INhAiToolCatalog>()
            .Select(NhAiToolCatalogViews.Of)
            .SelectMany(catalog => catalog.Descriptors)
            .Where(descriptor => (descriptor.Exposure & NhAiToolExposure.Agent) != 0)
            // The agent builder's tools are never a rule's: no turn but the builder's offers them.
            .Where(descriptor => NhAssistantAgentBuilderIds.MayOffer(definition.Id, descriptor.Id))
            .Where(descriptor => definition.ToolSelectors.Any(selector => NhAssistantTurnRunner.IsSelected(selector, descriptor.Id)))
            .Where(descriptor => descriptor.Effect == NhAiToolEffect.ReadOnly || NhAssistantTurnRunner.OffersEffectsToRules(definition))
            .GroupBy(descriptor => descriptor.Id, StringComparer.Ordinal)
            .Select(group => group.OrderByDescending(descriptor => descriptor.Version).First());
        return NhAssistantToolChains.StepToolsOf(descriptors, NhAssistantRuleScriptTool.Descriptor).ToArray();
    }

    /// <summary>
    /// The agent as its rules of the skill see it: with the skill's tool selectors when it names any.
    /// </summary>
    private static NhAssistantAgentDefinition DefinitionOf(
        NhAssistantAgentDefinition agent,
        NhAssistantAgentRunOptions options,
        string? skillId)
    {
        return skillId is not null && options.Skills.TryGetValue(skillId, out var skill) && skill.ToolSelectors.Count > 0
            ? agent with { ToolSelectors = skill.ToolSelectors }
            : agent;
    }

    /// <summary>
    /// Prepares the live version of a rule script an agent package carries (PK8), within the importing
    /// host's ceiling: the version to add, live or, under four eyes, as the candidate another administrator
    /// sets live; the live version it replaces; and the revision the import expects.
    /// </summary>
    internal async Task<TaskResult<NhAssistantRuleScriptImport>> PrepareImportAsync(
        string agentId,
        string agentSource,
        string? skillId,
        string? script,
        IReadOnlyList<string>? writes,
        string actorId,
        string? reason,
        CancellationToken cancellationToken)
    {
        var ceiling = NhAssistantRuleScriptRuntime.CeilingOf(registration, agentId, agentSource);
        if (ceiling is null || services.GetService<INhAssistantRuleScriptEngine>() is not { } engine)
        {
            return TaskResult<NhAssistantRuleScriptImport>.Failed(NotAllowed());
        }
        if (string.IsNullOrWhiteSpace(script)
            || script.Length > NhAssistantRuleScriptLimits.MaxScriptLength
            || engine.Check(agentId, script) is not null)
        {
            return TaskResult<NhAssistantRuleScriptImport>.Failed(NhAssistantValidationErrors.Failed("script", NhAssistantFieldErrors.Invalid));
        }

        var sorted = (writes ?? []).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (sorted.Length > NhAssistantRuleScriptLimits.MaxWritesPerVersion || sorted.Any(write => !ceiling.Writes.Contains(write)))
        {
            return TaskResult<NhAssistantRuleScriptImport>.Failed(new NhAssistantValidationErrors()
                .Add("writes", NhAssistantFieldErrors.NotAllowed)
                .ToResult(NhAssistantRuleScriptCodes.WriteNotAllowed, "A write is not one the code's ceiling allows for the agent's rule scripts."));
        }

        var ruleScript = await store.FindAsync(agentId, KeyOf(skillId), cancellationToken);
        var current = ruleScript?.Versions.FirstOrDefault(item => item.Status == NhAssistantRuleScriptStatuses.Live);
        var live = !registration.RuleScriptsAsData!.RequireFourEyes;
        var now = _applicationClock.GetUtcNow();
        var row = new AssistantRuleScriptVersion
        {
            Id = Guid.NewGuid(),
            AgentId = agentId,
            SkillId = KeyOf(skillId),
            Status = live ? NhAssistantRuleScriptStatuses.Live : NhAssistantRuleScriptStatuses.Candidate,
            Script = script,
            ScriptHash = NhAssistantAgentCatalog.ComputeHash(script),
            WritesJson = JsonSerializer.Serialize(sorted, Json),
            AuthorActorId = actorId,
            Reason = Trimmed(reason) is { Length: <= NhAssistantRuleScriptLimits.MaxReasonLength } kept ? kept : null,
            CreatedAt = now,
            LiveAt = live ? now : null,
            LiveBy = live ? actorId : null
        };
        return TaskResult<NhAssistantRuleScriptImport>.Succeeded(new NhAssistantRuleScriptImport(row, current, ruleScript?.Revision ?? 0));
    }

    /// <summary>
    /// The field errors of a version whose script or writes would differ from what an agent package locks
    /// (PK9), or <see langword="null"/>.
    /// </summary>
    private async Task<TaskResult?> LockedAsync(
        string agentId,
        string? skillId,
        string scriptHash,
        string writesJson,
        CancellationToken cancellationToken)
    {
        if (services.GetService<NhAssistantPackageLockCheck>() is not { } locks)
        {
            return null;
        }

        var (live, _) = await store.FindInEffectAsync(agentId, KeyOf(skillId), cancellationToken);
        var next = new AssistantRuleScriptVersion { ScriptHash = scriptHash, WritesJson = writesJson };
        return await locks.CheckChangeAsync(
            agentId,
            NhAssistantPackagePartKinds.RuleScript,
            KeyOf(skillId),
            live is null ? null : NhAssistantPackageFields.Of(live),
            NhAssistantPackageFields.Of(next),
            cancellationToken);
    }

    private async Task<TaskResult<(NhAssistantAgent Agent, NhAssistantAgentRunOptions Options)>> ResolveAsync(
        string agentId,
        string? skillId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        var agent = await agents.FindAsync(agentId, includeDisabled: true, cancellationToken);
        if (agent is null)
        {
            return TaskResult<(NhAssistantAgent, NhAssistantAgentRunOptions)>.Failed(NhAssistantErrorCodes.AgentNotFound, "The agent was not found.");
        }

        if (services.GetService<NhAssistantRunConfigurations>() is { } configurations)
        {
            await configurations.EnsureCurrentAsync(cancellationToken);
        }

        var options = registration.RunOptionsOf(agentId);
        if (skillId is not null && !options.Skills.ContainsKey(skillId))
        {
            return TaskResult<(NhAssistantAgent, NhAssistantAgentRunOptions)>.Failed(NhAssistantErrorCodes.SkillNotFound, "The skill was not found.");
        }

        return TaskResult<(NhAssistantAgent, NhAssistantAgentRunOptions)>.Succeeded((agent, options));
    }

    /// <summary>
    /// Checks what every status change needs before it reads the rule script: the agent or skill, the
    /// ceiling and the reason.
    /// </summary>
    private async Task<TaskResult> PrepareChangeAsync(string agentId, string? skillId, string? reason, CancellationToken cancellationToken)
    {
        var target = await ResolveAsync(agentId, skillId, cancellationToken);
        if (!target.Success)
        {
            return TaskResult.Failed(target);
        }
        if (NhAssistantRuleScriptRuntime.CeilingOf(registration, agentId, target.Data.Agent.Source) is null)
        {
            return NotAllowed();
        }
        if (Trimmed(reason)?.Length > NhAssistantRuleScriptLimits.MaxReasonLength)
        {
            return NhAssistantValidationErrors.Failed("reason", NhAssistantFieldErrors.TooLong);
        }

        return TaskResult.Succeeded();
    }

    private static IReadOnlyList<NhAiSubjectRef>? SubjectsOf(IReadOnlyList<NhAssistantRuleScriptSubjectDto>? subjects)
    {
        if (subjects is not { Count: > 0 and <= NhAssistantRuleScriptLimits.MaxDryRunSubjects })
        {
            return null;
        }

        var refs = new List<NhAiSubjectRef>(subjects.Count);
        foreach (var subject in subjects)
        {
            if (subject is null
                || !NhAiSubjectRef.IsValidType(subject.Type)
                || string.IsNullOrWhiteSpace(subject.Id)
                || subject.Id.Length > NhAiSubjectRef.MaxIdLength
                || subject.Id.Trim() != subject.Id)
            {
                return null;
            }

            var reference = new NhAiSubjectRef(subject.Type, subject.Id);
            if (!refs.Contains(reference))
            {
                refs.Add(reference);
            }
        }

        return refs;
    }

    private static void End(AssistantRuleScript ruleScript, int? version, string status, string actorId, DateTimeOffset now)
    {
        if (version is not { } number || VersionOf(ruleScript, number) is not { } ended)
        {
            return;
        }

        ended.Status = status;
        ended.EndedAt = now;
        ended.EndedBy = actorId;
    }

    private static AssistantRuleScriptVersion? VersionOf(AssistantRuleScript ruleScript, int version)
    {
        return ruleScript.Versions.FirstOrDefault(item => item.Version == version);
    }

    private static TaskResult<T>? Failure<T>(NhAssistantRuleScriptChange changed)
    {
        if (changed.IsConflict)
        {
            return TaskResult<T>.Failed(NhAssistantAdminErrorCodes.VersionConflict, "The rule script changed since it was read.");
        }

        return changed.Failure is { } failure ? TaskResult<T>.Failed(failure) : null;
    }

    private async Task AuditAsync(
        NhAssistantAuditEventKind kind,
        AssistantRuleScriptVersion version,
        NhAssistantRuleScriptAuthor actor,
        string outcome,
        CancellationToken cancellationToken)
    {
        await events.RuleScriptChangedAsync(
            kind,
            version.AgentId,
            version.SkillId.Length == 0 ? null : version.SkillId,
            actor.ActorId,
            actor.AgentId,
            version.Version,
            version.ScriptHash,
            outcome,
            cancellationToken);
    }

    private static NhAssistantRuleScriptVersionDto ToDto(AssistantRuleScriptVersion version, AssistantRuleScript ruleScript)
    {
        return new NhAssistantRuleScriptVersionDto(
            version.Version,
            version.Status,
            version.Script,
            version.ScriptHash,
            NhAssistantRuleScriptRuntime.WritesOf(version),
            version.AuthorActorId,
            version.AuthorAgentId,
            version.Reason,
            version.CreatedAt,
            version.LiveAt,
            version.LiveBy,
            version.EndedAt,
            version.EndedBy,
            ruleScript.CandidateShadow && ruleScript.CandidateVersion == version.Version,
            ruleScript.Revision);
    }

    private NhAssistantRuleScriptCeilingDto CeilingDto(NhAssistantRuleScriptAgentCeiling? ceiling)
    {
        return new NhAssistantRuleScriptCeilingDto(
            ceiling?.Writes.Order(StringComparer.Ordinal).ToArray() ?? [],
            ceiling?.MaxWritesPerRun ?? 0,
            ceiling?.Destructive.Order(StringComparer.Ordinal).ToArray() ?? [],
            registration.RuleScriptsAsData?.RequireFourEyes ?? false);
    }

    private static string? CodeRuleOf(NhAssistantAgentRunOptions options, string? skillId)
    {
        if (skillId is null)
        {
            return options.RuleHandlerType?.Name;
        }

        return options.Skills.TryGetValue(skillId, out var skill)
            ? skill.RuleHandlerName ?? skill.RuleHandlerType?.Name
            : null;
    }

    private static string KeyOf(string? skillId)
    {
        return skillId ?? string.Empty;
    }

    private static string? Trimmed(string? text)
    {
        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }

    private static void ValidateAuthor(NhAssistantRuleScriptAuthor author)
    {
        ArgumentNullException.ThrowIfNull(author);
        ArgumentException.ThrowIfNullOrWhiteSpace(author.ActorId);
        if (author.ActorId.Length > MaxActorIdLength)
        {
            throw new ArgumentOutOfRangeException(nameof(author), $"An actor id has at most {MaxActorIdLength} characters.");
        }
        if (author.AgentId is not null)
        {
            NhAssistantNames.ValidateSegment(author.AgentId, nameof(author));
        }
    }

    private static TaskResult NotAllowed()
    {
        return TaskResult.Failed(NhAssistantRuleScriptCodes.NotAllowed, "The code's ceiling does not let the agent have rule scripts.");
    }

    private static TaskResult VersionNotFound()
    {
        return TaskResult.Failed(NhAssistantRuleScriptCodes.VersionNotFound, "The rule script has no such version.");
    }

    private static TaskResult NotCandidate()
    {
        return TaskResult.Failed(NhAssistantRuleScriptCodes.NotCandidate, "Only a candidate goes live, runs in shadow or is discarded.");
    }

    private static TaskResult FourEyes()
    {
        return TaskResult.Failed(NhAssistantRuleScriptCodes.FourEyes, "Another administrator than the version's author sets it live.");
    }
}

/// <summary>
/// The line by line comparison of two scripts: the longest common subsequence of their lines.
/// </summary>
internal static class NhAssistantRuleScriptLines
{
    public static IReadOnlyList<NhAssistantRuleScriptLineDto> Compare(string other, string script)
    {
        var before = Split(other);
        var after = Split(script);
        var lengths = new int[before.Length + 1, after.Length + 1];
        for (var i = before.Length - 1; i >= 0; i--)
        {
            for (var j = after.Length - 1; j >= 0; j--)
            {
                lengths[i, j] = string.Equals(before[i], after[j], StringComparison.Ordinal)
                    ? lengths[i + 1, j + 1] + 1
                    : Math.Max(lengths[i + 1, j], lengths[i, j + 1]);
            }
        }

        var lines = new List<NhAssistantRuleScriptLineDto>(before.Length + after.Length);
        int a = 0, b = 0;
        while (a < before.Length || b < after.Length)
        {
            if (a < before.Length && b < after.Length && string.Equals(before[a], after[b], StringComparison.Ordinal))
            {
                lines.Add(new NhAssistantRuleScriptLineDto("same", b + 1, a + 1, after[b]));
                a++;
                b++;
            }
            else if (b < after.Length && (a >= before.Length || lengths[a, b + 1] >= lengths[a + 1, b]))
            {
                lines.Add(new NhAssistantRuleScriptLineDto("added", b + 1, null, after[b]));
                b++;
            }
            else
            {
                lines.Add(new NhAssistantRuleScriptLineDto("removed", null, a + 1, before[a]));
                a++;
            }
        }

        return lines;
    }

    private static string[] Split(string script)
    {
        return script.Length == 0 ? [] : script.ReplaceLineEndings("\n").Split('\n');
    }
}

/// <summary>
/// The live version of a rule script an agent package's import adds: the version, the live version it
/// replaces and the rule script's revision the import expects.
/// </summary>
internal sealed record NhAssistantRuleScriptImport(
    AssistantRuleScriptVersion Row,
    AssistantRuleScriptVersion? Current,
    int ExpectedRevision);
