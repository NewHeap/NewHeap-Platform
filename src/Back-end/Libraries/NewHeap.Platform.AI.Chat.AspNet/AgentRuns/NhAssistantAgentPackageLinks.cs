using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Chat.Persistence;

namespace NewHeap.Platform.AI.Chat.AspNet.AgentRuns;

/// <summary>
/// The package links the administration shows next to an agent and its parts (PK9, PK14): which
/// package a part came from, which of its fields stay read-only, and whether it changed locally since
/// the import, by comparing the hash of the part as the host has it now with the hash at import.
/// </summary>
internal sealed class NhAssistantAgentPackageLinks(
    NhAssistantAgentPackageStore packages,
    NhAssistantAdminStore admin,
    NhAssistantRunConfigurationStore runConfiguration,
    NhAssistantAgentMetadataStore metadata)
{
    /// <summary>
    /// The package an agent belongs to with its linked parts; <see langword="null"/> for an agent no
    /// package links.
    /// </summary>
    public async Task<NhAssistantAgentPackageLinkDto?> OfAgentAsync(string agentId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        var links = await packages.ListLinksAsync(agentId, cancellationToken);
        var package = await packages.FindPackageOfAgentAsync(agentId, cancellationToken);
        if (package is null && links.Count == 0)
        {
            return null;
        }

        var parts = await PartsAsync(agentId, links, cancellationToken);
        return new NhAssistantAgentPackageLinkDto(
            package?.Id ?? links[0].PackageId,
            package?.Version ?? links[0].PackageVersion,
            parts.Values.OrderBy(part => part.Part, StringComparer.Ordinal).ToArray());
    }

    /// <summary>
    /// The packages of several agents, for the list of agents: only agents with links or in a package
    /// are read further.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, NhAssistantAgentPackageLinkDto>> OfAgentsAsync(
        IReadOnlyCollection<string> agentIds,
        CancellationToken cancellationToken)
    {
        var linked = (await packages.ListLinksAsync(agentIds, cancellationToken))
            .Select(link => link.AgentId)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var result = new Dictionary<string, NhAssistantAgentPackageLinkDto>(StringComparer.Ordinal);
        foreach (var agentId in linked)
        {
            if (await OfAgentAsync(agentId, cancellationToken) is { } dto)
            {
                result[agentId] = dto;
            }
        }

        return result;
    }

    /// <summary>
    /// The link of every linked part of the agent, by part key.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, NhAssistantPackagePartLinkDto>> PartsOfAsync(
        string agentId,
        CancellationToken cancellationToken)
    {
        var links = await packages.ListLinksAsync(agentId, cancellationToken);
        return links.Count == 0
            ? new Dictionary<string, NhAssistantPackagePartLinkDto>(StringComparer.Ordinal)
            : await PartsAsync(agentId, links, cancellationToken);
    }

    private async Task<IReadOnlyDictionary<string, NhAssistantPackagePartLinkDto>> PartsAsync(
        string agentId,
        IReadOnlyList<AssistantAgentPackageLink> links,
        CancellationToken cancellationToken)
    {
        var hashes = await CurrentHashesAsync(agentId, links, cancellationToken);
        var parts = new Dictionary<string, NhAssistantPackagePartLinkDto>(StringComparer.Ordinal);
        foreach (var link in links)
        {
            var part = NhAssistantPackagePartKinds.PartOf(link.PartKind, link.PartKey);
            var current = hashes.GetValueOrDefault(part) ?? [];
            parts[part] = new NhAssistantPackagePartLinkDto(
                part,
                link.PackageId,
                link.PackageVersion,
                NhAssistantAgentPackageStore.LockedFieldsOf(link),
                link.Hash,
                // A candidate the import made is the package's version, not a local change.
                !current.Contains(link.Hash, StringComparer.Ordinal),
                link.LinkedAt,
                link.LinkedBy);
        }

        return parts;
    }

    /// <summary>
    /// The hashes of the linked parts as the host has them now, by part key: of a skill or trigger its
    /// live version and its candidate.
    /// </summary>
    private async Task<IReadOnlyDictionary<string, IReadOnlyList<string>>> CurrentHashesAsync(
        string agentId,
        IReadOnlyList<AssistantAgentPackageLink> links,
        CancellationToken cancellationToken)
    {
        var kinds = links.Select(link => link.PartKind).ToHashSet(StringComparer.Ordinal);
        var hashes = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        if (kinds.Contains(NhAssistantPackagePartKinds.Agent)
            && await admin.FindAgentAsync(agentId, cancellationToken) is { } row
            && NhAssistantAgentPackageParts.DefinitionOf(row) is { } definition)
        {
            hashes[NhAssistantPackagePartKinds.Agent] = [NhAssistantAgentPackageFormat.HashOf(definition)];
        }
        if (kinds.Contains(NhAssistantPackagePartKinds.RunSettings)
            && await runConfiguration.FindRunSettingsAsync(agentId, cancellationToken) is { } settings)
        {
            hashes[NhAssistantPackagePartKinds.RunSettings] = [NhAssistantAgentPackageFormat.HashOf(NhAssistantAgentPackageParts.RunSettingsOf(settings))];
        }
        if (kinds.Contains(NhAssistantPackagePartKinds.Skill))
        {
            foreach (var group in (await runConfiguration.ListSkillsAsync(agentId, cancellationToken))
                         .Where(item => item.Status is NhAssistantSkillDefinitionStatuses.Live or NhAssistantSkillDefinitionStatuses.Candidate)
                         .GroupBy(item => item.SkillId, StringComparer.Ordinal))
            {
                hashes[NhAssistantPackagePartKinds.PartOf(NhAssistantPackagePartKinds.Skill, group.Key)] = group
                    .Select(item => NhAssistantAgentPackageFormat.HashOf(NhAssistantAgentPackageParts.SkillOf(item)))
                    .ToArray();
            }
        }
        if (kinds.Contains(NhAssistantPackagePartKinds.Trigger))
        {
            foreach (var group in (await runConfiguration.ListTriggersAsync(agentId, cancellationToken))
                         .Where(item => item.Status is NhAssistantSkillDefinitionStatuses.Live or NhAssistantSkillDefinitionStatuses.Candidate)
                         .GroupBy(item => item.Id, StringComparer.Ordinal))
            {
                hashes[NhAssistantPackagePartKinds.PartOf(NhAssistantPackagePartKinds.Trigger, group.Key)] = group
                    .Select(item => NhAssistantAgentPackageFormat.HashOf(NhAssistantAgentPackageParts.TriggerOf(item)))
                    .ToArray();
            }
        }
        if (kinds.Contains(NhAssistantPackagePartKinds.Consultation))
        {
            foreach (var item in await runConfiguration.ListConsultationsAsync(agentId, cancellationToken))
            {
                hashes[NhAssistantPackagePartKinds.PartOf(NhAssistantPackagePartKinds.Consultation, item.SubjectType)] =
                    [NhAssistantAgentPackageFormat.HashOf(NhAssistantAgentPackageParts.ConsultationOf(item))];
            }
        }
        if (kinds.Contains(NhAssistantPackagePartKinds.HandOff))
        {
            foreach (var item in await runConfiguration.ListHandOffsAsync(agentId, cancellationToken))
            {
                hashes[NhAssistantPackagePartKinds.PartOf(NhAssistantPackagePartKinds.HandOff, item.SubjectType)] =
                    [NhAssistantAgentPackageFormat.HashOf(NhAssistantAgentPackageParts.HandOffOf(item))];
            }
        }
        if (kinds.Contains(NhAssistantPackagePartKinds.Metadata))
        {
            foreach (var item in await metadata.ListAsync(agentId, cancellationToken))
            {
                hashes[NhAssistantPackagePartKinds.PartOf(NhAssistantPackagePartKinds.Metadata, item.Name)] =
                    [NhAssistantAgentPackageFormat.HashOf(NhAssistantAgentPackageParts.MetadataOf(item.ValueJson))];
            }
        }

        return hashes;
    }
}
