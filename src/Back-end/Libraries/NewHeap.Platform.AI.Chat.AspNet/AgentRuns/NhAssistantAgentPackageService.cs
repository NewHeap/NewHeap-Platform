using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.AI.Chat.Runs;
using NewHeap.Platform.Common.Models;

namespace NewHeap.Platform.AI.Chat.AspNet.AgentRuns;

/// <inheritdoc cref="INhAssistantAgentPackages"/>
internal sealed partial class NhAssistantAgentPackageService(
    NhAssistantRegistrationState registration,
    NhAssistantRunConfigurations configurations,
    NhAssistantRunConfigurationStore runConfiguration,
    NhAssistantAgentPackageStore packages,
    NhAssistantAgentPackageLinks links,
    NhAssistantAdminStore admin,
    NhAssistantAgentMetadataStore metadataStore,
    NhAssistantAgentCatalog catalog,
    NhAssistantAgentEvents events,
    NhAssistantInstructionAssets instructionAssets,
    NhAssistantRunStore runs,
    IServiceProvider services) : INhAssistantAgentPackages
{
    private const int MaxActorIdLength = 256;

    /// <summary>
    /// The actor a preview checks the package as; nothing is stored for it.
    /// </summary>
    private const string PreviewActor = "assistant-package-preview";

    public async Task<TaskResult<NhAssistantAgentPackageExportDto>> ExportAsync(
        IReadOnlyList<string> agentIds,
        NhAssistantAgentPackageExportOptions options,
        string actorId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(agentIds);
        ArgumentNullException.ThrowIfNull(options);
        ValidateActor(actorId);
        var ids = NormalizeAgentIds(agentIds);
        var errors = CheckAgentIds(ids)
            .Require(options.Description is not { Length: > NhAssistantAgentPackageFormat.MaxDescriptionLength }, "description", NhAssistantFieldErrors.TooLong)
            .Require(options.Version is null or >= 1, "version", NhAssistantFieldErrors.Invalid)
            .Require(
                options.PackageId is null
                || (NhAssistantNames.IsSegment(options.PackageId) && options.PackageId.Length <= NhAssistantAgentPackageFormat.MaxIdLength),
                "packageId",
                NhAssistantFieldErrors.Invalid);
        if (!errors.IsEmpty)
        {
            return TaskResult<NhAssistantAgentPackageExportDto>.Failed(errors.ToResult());
        }

        var rows = await FindAgentRowsAsync(ids, cancellationToken);
        if (rows is null)
        {
            return TaskResult<NhAssistantAgentPackageExportDto>.Failed(NhAssistantErrorCodes.AgentNotFound, "The agent was not found.");
        }

        // Exporting linked agents keeps their package (PK13); agents of two packages travel apart.
        var linkedTo = new List<AssistantAgentPackage>();
        foreach (var id in ids)
        {
            if (await packages.FindPackageOfAgentAsync(id, cancellationToken) is { } linked
                && linkedTo.All(item => item.Id != linked.Id))
            {
                linkedTo.Add(linked);
            }
        }
        if (linkedTo.Count > 1)
        {
            return TaskResult<NhAssistantAgentPackageExportDto>.Failed(NhAssistantValidationErrors.Failed("agentIds", NhAssistantFieldErrors.Invalid));
        }

        var installed = linkedTo.FirstOrDefault();
        var packageId = options.PackageId ?? installed?.Id ?? ids[0];
        if (installed is not null && !string.Equals(installed.Id, packageId, StringComparison.Ordinal))
        {
            return TaskResult<NhAssistantAgentPackageExportDto>.Failed(NhAssistantValidationErrors.Failed("packageId", NhAssistantFieldErrors.Invalid));
        }

        var version = options.Version ?? (installed is null ? 1 : installed.Version + 1);
        var exported = new List<NhAssistantPackageAgent>(rows.Count);
        var lockErrors = new NhAssistantValidationErrors();
        foreach (var row in rows)
        {
            var agent = await ReadAgentAsync(row, cancellationToken);
            var locks = options.Locks?.GetValueOrDefault(row.Id);
            if (locks is not null)
            {
                CheckLocks(agent, locks, lockErrors, "locks." + row.Id);
            }

            exported.Add(agent with
            {
                Locks = locks is null || locks.Count == 0 ? null : NormalizeLocks(locks)
            });
        }
        if (!lockErrors.IsEmpty)
        {
            return TaskResult<NhAssistantAgentPackageExportDto>.Failed(lockErrors.ToResult());
        }
        if (options.Locks is { } allLocks && allLocks.Keys.Any(id => !ids.Contains(id, StringComparer.Ordinal)))
        {
            return TaskResult<NhAssistantAgentPackageExportDto>.Failed(NhAssistantValidationErrors.Failed("locks", NhAssistantFieldErrors.Invalid));
        }

        var package = new NhAssistantAgentPackage(
            NhAssistantAgentPackageFormat.Name,
            NhAssistantAgentPackageFormat.Version,
            new NhAssistantAgentPackageInfo(packageId, version, string.IsNullOrWhiteSpace(options.Description) ? null : options.Description.Trim()),
            exported);
        if (Encoding.UTF8.GetByteCount(NhAssistantAgentPackageFormat.Write(package)) > NhAssistantAgentPackageFormat.MaxBytes)
        {
            return TaskResult<NhAssistantAgentPackageExportDto>.Failed(
                NhAssistantErrorCodes.PackageTooLarge,
                "The agent package is larger than 1 MB or holds more than 20 agents.");
        }

        var hash = NhAssistantAgentPackageFormat.HashOf(package);
        var warnings = await WarningsOfAsync(package, cancellationToken);
        await events.PackageExportedAsync(packageId, version, hash, exported.Count, actorId, cancellationToken);
        return TaskResult<NhAssistantAgentPackageExportDto>.Succeeded(new NhAssistantAgentPackageExportDto(package, hash, warnings));
    }

    public async Task<TaskResult<NhAssistantAgentPackagePreviewDto>> PreviewImportAsync(
        NhAssistantAgentPackage package,
        NhAssistantAgentPackageImportOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var planned = await PlanAsync(package, options, PreviewActor, cancellationToken);
        if (planned.Failure is { } failure)
        {
            return TaskResult<NhAssistantAgentPackagePreviewDto>.Failed(failure);
        }

        var plan = planned.Plan!;
        return TaskResult<NhAssistantAgentPackagePreviewDto>.Succeeded(new NhAssistantAgentPackagePreviewDto(
            plan.Package.Package.Id,
            plan.Package.Package.Version,
            plan.Installed?.Version,
            plan.Importable,
            plan.Parts.Select(part => part.ToDto()).ToArray()));
    }

    public async Task<TaskResult<NhAssistantAgentPackageImportDto>> ImportAsync(
        NhAssistantAgentPackage package,
        NhAssistantAgentPackageImportOptions options,
        string actorId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ValidateActor(actorId);
        var planned = await PlanAsync(package, options, actorId, cancellationToken);
        if (planned.Failure is { } failure)
        {
            return TaskResult<NhAssistantAgentPackageImportDto>.Failed(failure);
        }

        var plan = planned.Plan!;
        if (!plan.Importable)
        {
            return TaskResult<NhAssistantAgentPackageImportDto>.Failed(Refusal());
        }

        var written = await WriteAsync(plan, actorId, cancellationToken);
        if (!written)
        {
            return TaskResult<NhAssistantAgentPackageImportDto>.Failed(
                NhAssistantAdminErrorCodes.VersionConflict,
                "The agents or the package changed in the meantime; nothing was imported.");
        }

        await AuditImportAsync(plan, actorId, cancellationToken);
        await configurations.ReloadAsync(cancellationToken);
        return TaskResult<NhAssistantAgentPackageImportDto>.Succeeded(new NhAssistantAgentPackageImportDto(
            plan.Package.Package.Id,
            plan.Package.Package.Version,
            plan.Hash,
            plan.Parts.Select(part => part.ToDto()).ToArray()));
    }

    public async Task<TaskResult<NhAssistantAgentPackageUnlinkDto>> UnlinkAsync(
        string agentId,
        string? part,
        string actorId,
        string? reason = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ValidateActor(actorId);
        reason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        var errors = new NhAssistantValidationErrors()
            .Require(reason is not { Length: > NhAssistantLimits.MaxStateReasonCharacters }, "reason", NhAssistantFieldErrors.TooLong);
        string? kind = null;
        string? key = null;
        if (part is not null)
        {
            var known = NhAssistantPackagePartKinds.TryParse(part, out var partKind, out var partKey);
            errors.Require(known, "part", NhAssistantFieldErrors.Invalid);
            kind = partKind;
            key = partKey;
        }
        if (!errors.IsEmpty)
        {
            return TaskResult<NhAssistantAgentPackageUnlinkDto>.Failed(errors.ToResult());
        }
        if (await catalog.FindAsync(agentId, includeDisabled: true, cancellationToken) is null)
        {
            return TaskResult<NhAssistantAgentPackageUnlinkDto>.Failed(NhAssistantErrorCodes.AgentNotFound, "The agent was not found.");
        }

        var unlinked = await packages.UnlinkAsync(agentId, kind, key, cancellationToken);
        if (unlinked.Outcome == NhAssistantStoreOutcome.NotFound)
        {
            return TaskResult<NhAssistantAgentPackageUnlinkDto>.Failed(NhAssistantErrorCodes.PackageNotLinked, "The agent or part is not linked to a package.");
        }
        if (!unlinked.Succeeded)
        {
            return TaskResult<NhAssistantAgentPackageUnlinkDto>.Failed(NhAssistantAdminErrorCodes.VersionConflict, "The package changed in the meantime.");
        }

        var (packageId, packageVersion, removed) = unlinked.Value;
        var parts = removed
            .Select(link => NhAssistantPackagePartKinds.PartOf(link.PartKind, link.PartKey))
            .Order(StringComparer.Ordinal)
            .ToArray();
        await events.PackageUnlinkedAsync(
            agentId,
            part ?? NhAssistantPackagePartKinds.WholeAgent,
            packageId,
            packageVersion,
            NhAssistantAgentPackageAudit.Unlinked,
            actorId,
            cancellationToken);
        return TaskResult<NhAssistantAgentPackageUnlinkDto>.Succeeded(new NhAssistantAgentPackageUnlinkDto(agentId, packageId, parts));
    }

    public Task<NhAssistantAgentPackageLinkDto?> GetLinkAsync(string agentId, CancellationToken cancellationToken = default)
    {
        return links.OfAgentAsync(agentId, cancellationToken);
    }

    public async Task<NhAssistantInstalledAgentPackageDto?> GetPackageAsync(string packageId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(packageId);
        // Only a well-formed id can name a package, so a provider whose comparison ignores case finds
        // nothing another provider would not.
        if (!NhAssistantNames.IsSegment(packageId) || packageId.Length > NhAssistantAgentPackageFormat.MaxIdLength)
        {
            return null;
        }

        var package = await packages.FindPackageAsync(packageId, cancellationToken);
        if (package is null)
        {
            return null;
        }

        return new NhAssistantInstalledAgentPackageDto(
            package.Id,
            package.Version,
            package.FormatVersion,
            package.Hash,
            NhAssistantAgentPackageStore.AgentIdsOf(package),
            package.ImportedAt,
            package.ImportedBy,
            await links.OfPackageAsync(package.Id, cancellationToken));
    }

    public async Task<TaskResult<IReadOnlyList<NhAssistantPackageAgent>>> ReadAsync(
        IReadOnlyList<string> agentIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(agentIds);
        var ids = NormalizeAgentIds(agentIds);
        var errors = CheckAgentIds(ids);
        if (!errors.IsEmpty)
        {
            return TaskResult<IReadOnlyList<NhAssistantPackageAgent>>.Failed(errors.ToResult());
        }

        var rows = await FindAgentRowsAsync(ids, cancellationToken);
        if (rows is null)
        {
            return TaskResult<IReadOnlyList<NhAssistantPackageAgent>>.Failed(NhAssistantErrorCodes.AgentNotFound, "The agent was not found.");
        }

        // The builders of an export, without its locks and without its audit.
        var agents = new NhAssistantPackageAgent[rows.Count];
        for (var index = 0; index < rows.Count; index++)
        {
            agents[index] = await ReadAgentAsync(rows[index], cancellationToken);
        }

        IReadOnlyList<NhAssistantPackageAgent> read = agents;
        return TaskResult<IReadOnlyList<NhAssistantPackageAgent>>.Succeeded(read);
    }

    /// <summary>
    /// The agent ids an export or a read names, trimmed; a missing id is empty, which is invalid.
    /// </summary>
    private static string[] NormalizeAgentIds(IReadOnlyList<string> agentIds)
    {
        return agentIds.Select(id => id?.Trim() ?? string.Empty).ToArray();
    }

    /// <summary>
    /// The checks of the agent ids of an export or a read: 1 to
    /// <see cref="NhAssistantAgentPackageFormat.MaxAgents"/> distinct agent ids.
    /// </summary>
    private static NhAssistantValidationErrors CheckAgentIds(IReadOnlyList<string> ids)
    {
        return new NhAssistantValidationErrors()
            .Require(ids.Count > 0, "agentIds", NhAssistantFieldErrors.Required)
            .Require(ids.Count <= NhAssistantAgentPackageFormat.MaxAgents, "agentIds", NhAssistantFieldErrors.TooLong)
            .Require(
                ids.All(NhAssistantNames.IsSegment) && ids.Distinct(StringComparer.Ordinal).Count() == ids.Count,
                "agentIds",
                NhAssistantFieldErrors.Invalid);
    }

    /// <summary>
    /// The rows of the agents in the order of <paramref name="ids"/>, once the agents in code are
    /// stored; <see langword="null"/> when one of them does not exist.
    /// </summary>
    private async Task<IReadOnlyList<AssistantAgent>?> FindAgentRowsAsync(IReadOnlyList<string> ids, CancellationToken cancellationToken)
    {
        await catalog.ListAsync(includeDisabled: true, cancellationToken);
        var rows = new List<AssistantAgent>(ids.Count);
        foreach (var id in ids)
        {
            var row = await admin.FindAgentAsync(id, cancellationToken);
            if (row is null || await catalog.FindAsync(id, includeDisabled: true, cancellationToken) is null)
            {
                return null;
            }

            rows.Add(row);
        }

        return rows;
    }

    /// <summary>
    /// One agent as an export puts it in a package, with the hash of every part and without locks.
    /// </summary>
    private async Task<NhAssistantPackageAgent> ReadAgentAsync(AssistantAgent row, CancellationToken cancellationToken)
    {
        var agent = await ExportAgentAsync(row, cancellationToken);
        return agent with { Hashes = HashesOf(agent) };
    }

    /// <summary>
    /// One agent as the package carries it: an agent created in the administration whole, an agent
    /// in code as the overlay of its data.
    /// </summary>
    private async Task<NhAssistantPackageAgent> ExportAgentAsync(AssistantAgent row, CancellationToken cancellationToken)
    {
        var dataAgent = row.Source == NhAssistantAgentSources.Admin;
        var settings = dataAgent ? await runConfiguration.FindRunSettingsAsync(row.Id, cancellationToken) : null;
        var skills = (await runConfiguration.ListSkillsAsync(row.Id, cancellationToken))
            .Where(item => item.Status == NhAssistantSkillDefinitionStatuses.Live)
            .OrderBy(item => item.SkillId, StringComparer.Ordinal)
            .Select(NhAssistantAgentPackageParts.SkillOf)
            .ToArray();
        var triggers = (await runConfiguration.ListTriggersAsync(row.Id, cancellationToken))
            .Where(item => item.Status == NhAssistantSkillDefinitionStatuses.Live)
            .OrderBy(item => item.Id, StringComparer.Ordinal)
            .Select(NhAssistantAgentPackageParts.TriggerOf)
            .ToArray();
        var consultations = (await runConfiguration.ListConsultationsAsync(row.Id, cancellationToken))
            .Select(NhAssistantAgentPackageParts.ConsultationOf)
            .ToArray();
        var handOffs = (await runConfiguration.ListHandOffsAsync(row.Id, cancellationToken))
            .Select(NhAssistantAgentPackageParts.HandOffOf)
            .ToArray();
        // An agent in code carries only its overrides of editable entries.
        var metadata = (await metadataStore.ListAsync(row.Id, cancellationToken))
            .Where(item => dataAgent || registration.AgentMetadata.GetValueOrDefault(item.Name)?.Editable == true)
            .ToDictionary(item => item.Name, item => NhAssistantAgentPackageParts.MetadataOf(item.ValueJson), StringComparer.Ordinal);
        var groups = (await services.GetRequiredService<NhAssistantAgentGroups>().MembershipsAsync([row.Id], cancellationToken))[row.Id].Groups;
        return new NhAssistantPackageAgent(
            row.Id,
            dataAgent ? NhAssistantAgentSources.Admin : NhAssistantAgentSources.Code,
            NhAssistantAgentPackageParts.DefinitionOf(row),
            settings is null ? null : NhAssistantAgentPackageParts.RunSettingsOf(settings),
            skills.Length == 0 ? null : skills,
            triggers.Length == 0 ? null : triggers,
            consultations.Length == 0 ? null : consultations,
            handOffs.Length == 0 ? null : handOffs,
            metadata.Count == 0 ? null : metadata,
            groups.Count == 0 ? null : groups);
    }

    /// <summary>
    /// Every reference of the package's agents to an agent outside it (PK18).
    /// </summary>
    private async Task<IReadOnlyList<NhAssistantAgentPackageWarningDto>> WarningsOfAsync(
        NhAssistantAgentPackage package,
        CancellationToken cancellationToken)
    {
        var inside = package.Agents.Select(agent => agent.Id).ToHashSet(StringComparer.Ordinal);
        var all = await catalog.ListAsync(includeDisabled: true, cancellationToken);
        var warnings = new List<NhAssistantAgentPackageWarningDto>();
        foreach (var agent in package.Agents)
        {
            foreach (var handOff in agent.HandOffs ?? [])
            {
                foreach (var source in handOff.Sources.Where(source => !inside.Contains(source)))
                {
                    warnings.Add(new NhAssistantAgentPackageWarningDto(
                        agent.Id,
                        NhAssistantPackagePartKinds.PartOf(NhAssistantPackagePartKinds.HandOff, handOff.SubjectType),
                        NhAssistantAgentPackageWarnings.HandOffSource,
                        source));
                }
            }
            if (agent.Definition?.ShadowOf is { } liveAgentId && !inside.Contains(liveAgentId))
            {
                warnings.Add(new NhAssistantAgentPackageWarningDto(agent.Id, NhAssistantPackagePartKinds.Agent, NhAssistantAgentPackageWarnings.ShadowOf, liveAgentId));
            }
            foreach (var consultation in agent.Consultations ?? [])
            {
                // Askers of a consultation that takes requests only select its request tool.
                var toolId = consultation.RequestsOnly == true
                    ? NhAssistantConsultationTools.RequestToolId(agent.Id, consultation.SubjectType)
                    : NhAssistantConsultationTools.ConsultToolId(agent.Id, consultation.SubjectType);
                foreach (var asker in all.Where(candidate => !inside.Contains(candidate.Id)
                                                             && candidate.Definition.ToolSelectors.Any(selector => NhAssistantRegistrationState.Covers(selector, toolId))))
                {
                    warnings.Add(new NhAssistantAgentPackageWarningDto(
                        agent.Id,
                        NhAssistantPackagePartKinds.PartOf(NhAssistantPackagePartKinds.Consultation, consultation.SubjectType),
                        NhAssistantAgentPackageWarnings.ConsultationAsker,
                        asker.Id));
                }
            }
            foreach (var variant in all.Where(candidate => !inside.Contains(candidate.Id)
                                                           && string.Equals(candidate.ShadowOfAgentId, agent.Id, StringComparison.Ordinal)))
            {
                warnings.Add(new NhAssistantAgentPackageWarningDto(agent.Id, NhAssistantPackagePartKinds.Agent, NhAssistantAgentPackageWarnings.ShadowVariant, variant.Id));
            }
        }

        return warnings;
    }

    /// <summary>
    /// The SHA-256 of every part an agent carries, by part key.
    /// </summary>
    private static IReadOnlyDictionary<string, string> HashesOf(NhAssistantPackageAgent agent)
    {
        var hashes = new SortedDictionary<string, string>(StringComparer.Ordinal);
        if (agent.Definition is { } definition)
        {
            hashes[NhAssistantPackagePartKinds.Agent] = NhAssistantAgentPackageFormat.HashOf(definition);
        }
        if (agent.RunSettings is { } settings)
        {
            hashes[NhAssistantPackagePartKinds.RunSettings] = NhAssistantAgentPackageFormat.HashOf(settings);
        }
        foreach (var skill in agent.Skills ?? [])
        {
            hashes[NhAssistantPackagePartKinds.PartOf(NhAssistantPackagePartKinds.Skill, skill.Id)] = NhAssistantAgentPackageFormat.HashOf(skill);
        }
        foreach (var trigger in agent.Triggers ?? [])
        {
            hashes[NhAssistantPackagePartKinds.PartOf(NhAssistantPackagePartKinds.Trigger, trigger.Id)] = NhAssistantAgentPackageFormat.HashOf(trigger);
        }
        foreach (var consultation in agent.Consultations ?? [])
        {
            hashes[NhAssistantPackagePartKinds.PartOf(NhAssistantPackagePartKinds.Consultation, consultation.SubjectType)] = NhAssistantAgentPackageFormat.HashOf(consultation);
        }
        foreach (var handOff in agent.HandOffs ?? [])
        {
            hashes[NhAssistantPackagePartKinds.PartOf(NhAssistantPackagePartKinds.HandOff, handOff.SubjectType)] = NhAssistantAgentPackageFormat.HashOf(handOff);
        }
        foreach (var (name, value) in agent.Metadata ?? new Dictionary<string, JsonElement>())
        {
            hashes[NhAssistantPackagePartKinds.PartOf(NhAssistantPackagePartKinds.Metadata, name)] = NhAssistantAgentPackageFormat.HashOf(value);
        }

        return hashes;
    }

    /// <summary>
    /// The part keys an agent of a package carries.
    /// </summary>
    private static IReadOnlyList<string> PartsOf(NhAssistantPackageAgent agent)
    {
        return HashesOf(agent).Keys.ToArray();
    }

    /// <summary>
    /// Checks the locks of one agent: every key a part the agent carries, every list <c>*</c> alone or
    /// distinct fields of its kind; an agent in code locks only the fields it may override.
    /// </summary>
    private static void CheckLocks(
        NhAssistantPackageAgent agent,
        IReadOnlyDictionary<string, IReadOnlyList<string>> locks,
        NhAssistantValidationErrors errors,
        string field)
    {
        var parts = PartsOf(agent);
        foreach (var (part, fields) in locks)
        {
            if (!parts.Contains(part, StringComparer.Ordinal)
                || !NhAssistantPackagePartKinds.TryParse(part, out var kind, out _)
                || fields is null
                || fields.Count == 0)
            {
                errors.Add(field, NhAssistantFieldErrors.Invalid);
                continue;
            }

            var lockable = NhAssistantPackageLockFields.Of(kind);
            if (kind == NhAssistantPackagePartKinds.Agent && agent.Source != NhAssistantAgentSources.Admin)
            {
                lockable = lockable.Take(6).ToArray();
            }

            var valid = fields.Count == 1 && fields[0] == NhAssistantPackageLockFields.All
                        || (fields.All(lockable.Contains) && fields.Distinct(StringComparer.Ordinal).Count() == fields.Count);
            errors.Require(valid, field, NhAssistantFieldErrors.Invalid);
        }
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<string>> NormalizeLocks(IReadOnlyDictionary<string, IReadOnlyList<string>> locks)
    {
        return new SortedDictionary<string, IReadOnlyList<string>>(
            locks.ToDictionary(item => item.Key, item => (IReadOnlyList<string>)item.Value.ToArray(), StringComparer.Ordinal),
            StringComparer.Ordinal);
    }

    /// <summary>
    /// The failure of an import whose check refused a part: <c>assistant-package-refused</c> with the
    /// plain field error <c>parts: invalid</c>, which every client of the error answer reads. Why each
    /// part was refused is what the preview of the same package reports per part.
    /// </summary>
    private static TaskResult Refusal()
    {
        return new NhAssistantValidationErrors()
            .Add("parts", NhAssistantFieldErrors.Invalid)
            .ToResult(NhAssistantErrorCodes.PackageRefused, "The agent package was refused; nothing was imported.");
    }

    private static void ValidateActor(string actorId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        if (actorId.Length > MaxActorIdLength)
        {
            throw new ArgumentOutOfRangeException(nameof(actorId), $"An actor id has at most {MaxActorIdLength} characters.");
        }
    }
}

/// <summary>
/// The result codes of the audit of agent packages.
/// </summary>
internal static class NhAssistantAgentPackageAudit
{
    public const string Exported = "exported";

    public const string Created = "created";

    public const string Upgraded = "upgraded";

    public const string Unlinked = "unlinked";

    public const string LeftBehind = "left-behind";
}
