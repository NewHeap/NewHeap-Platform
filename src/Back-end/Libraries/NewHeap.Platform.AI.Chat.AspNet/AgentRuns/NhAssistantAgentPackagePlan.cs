using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.Common.Models;

namespace NewHeap.Platform.AI.Chat.AspNet.AgentRuns;

/// <summary>
/// What an import of a package does on this host (PK6): every agent with the row it would have and
/// every part with its outcome, the row it would store and the version it expects, checked as a save
/// checks it and once more as a whole against the host as it would be after the import (PK17).
/// </summary>
internal sealed class NhAssistantPackagePlan(NhAssistantAgentPackage package, string hash, AssistantAgentPackage? installed)
{
    public NhAssistantAgentPackage Package { get; } = package;

    /// <summary>
    /// The SHA-256 of the package's canonical JSON.
    /// </summary>
    public string Hash { get; } = hash;

    /// <summary>
    /// The version of the package the host has; <see langword="null"/> for a package it does not have.
    /// </summary>
    public AssistantAgentPackage? Installed { get; } = installed;

    public List<NhAssistantPlannedAgent> Agents { get; } = [];

    /// <summary>
    /// Every part in dependency order per agent, agents left behind last.
    /// </summary>
    public List<NhAssistantPlannedPart> Parts { get; } = [];

    /// <summary>
    /// The agents of the installed package the package no longer contains.
    /// </summary>
    public List<string> LeftBehindAgents { get; } = [];

    /// <summary>
    /// The promotion of a package-linked shadow variant this plan applies to its live agent
    /// (FB11); <see langword="null"/> for an import.
    /// </summary>
    public NhAssistantPackagePromotion? Promotion { get; init; }

    public bool Importable => Parts.All(part => part.Outcome != NhAssistantPackagePartOutcomes.Refused);

    public IEnumerable<NhAssistantPlannedPart> PartsOf(string agentId, string kind)
    {
        return Parts.Where(part => part.AgentId == agentId && part.Kind == kind);
    }
}

/// <summary>
/// A shadow variant whose parts all belong to one package, promoted as that package's version onto
/// its live agent (FB11): the plan applies the variant's parts to the live agent with the import's
/// rules, and the write deletes the variant in the same transaction.
/// </summary>
/// <param name="VariantId">The variant, which the promotion deletes.</param>
/// <param name="VariantVersion">The variant's version the promotion was decided on.</param>
/// <param name="LiveAgentId">The live agent that takes the package version.</param>
/// <param name="Reason">Stored with the versions of the parts it saves; <see langword="null"/> uses the
/// import's.</param>
internal sealed record NhAssistantPackagePromotion(string VariantId, int VariantVersion, string LiveAgentId, string? Reason);

/// <summary>
/// One agent of a package as the import plans it.
/// </summary>
internal sealed class NhAssistantPlannedAgent(NhAssistantPackageAgent source, AssistantAgent? hostRow)
{
    public NhAssistantPackageAgent Source { get; } = source;

    public string Id => Source.Id;

    /// <summary>
    /// The package carries the agent as an overlay of an agent in code.
    /// </summary>
    public bool InCode => Source.Source == NhAssistantAgentSources.Code;

    /// <summary>
    /// The agent as the host has it; <see langword="null"/> for one the import creates.
    /// </summary>
    public AssistantAgent? HostRow { get; } = hostRow;

    /// <summary>
    /// The agent belongs to the installed package, or the import relinks it as a whole: its parts
    /// without a link are local.
    /// </summary>
    public bool Member { get; set; }

    /// <summary>
    /// The shadow variant whose package version the agent takes (FB11): its links to another package
    /// end, and the variant's trigger ids become its own; <see langword="null"/> for an import.
    /// </summary>
    public string? PromotedVariantId { get; init; }

    /// <summary>
    /// The input the definition maps to.
    /// </summary>
    public NhAssistantAgentInput? Input { get; set; }

    /// <summary>
    /// The agent's row as it would be after the import.
    /// </summary>
    public AssistantAgent? Row { get; set; }

    /// <summary>
    /// The rows of the metadata of an agent the import creates.
    /// </summary>
    public IReadOnlyList<AssistantAgentMetadata> NewMetadataRows { get; set; } = [];

    /// <summary>
    /// The checked values of the metadata of an agent created in the administration the import changes.
    /// </summary>
    public IReadOnlyDictionary<string, NhAssistantAgentMetadataCheck?> MetadataValues { get; set; } =
        new Dictionary<string, NhAssistantAgentMetadataCheck?>(StringComparer.Ordinal);

    public IReadOnlyList<AssistantAgentMetadata> HostMetadata { get; set; } = [];

    /// <summary>
    /// The links of the agent's parts to the package.
    /// </summary>
    public IReadOnlyList<AssistantAgentPackageLink> Links { get; set; } = [];

    /// <summary>
    /// The agent's row after the import changed it, and what its metadata became.
    /// </summary>
    public AssistantAgent? UpdatedRow { get; set; }

    public List<Persistence.NhAssistantAgentMetadataChange> MetadataChanges { get; } = [];
}

/// <summary>
/// One part of an agent as the import plans it, with its outcome (PK6).
/// </summary>
internal sealed class NhAssistantPlannedPart(string agentId, string kind, string key)
{
    public string AgentId { get; } = agentId;

    public string Kind { get; } = kind;

    public string Key { get; } = key;

    public string Part => NhAssistantPackagePartKinds.PartOf(Kind, Key);

    public string Outcome { get; set; } = NhAssistantPackagePartOutcomes.Created;

    public bool ChangedLocally { get; set; }

    public string? Code { get; private set; }

    public SortedDictionary<string, List<string>> Errors { get; } = new(StringComparer.Ordinal);

    public IReadOnlyList<string> LockedFields { get; set; } = [];

    public AssistantAgentPackageLink? Link { get; set; }

    /// <summary>
    /// The part as the host has it: a row of the part's kind, or a metadata row.
    /// </summary>
    public object? Current { get; set; }

    /// <summary>
    /// The row the import stores: of the part's kind, or the checked value of a metadata entry.
    /// </summary>
    public object? Row { get; set; }

    public int? ExpectedVersion { get; set; }

    /// <summary>
    /// A changed skill or trigger becomes the candidate next to its live version (<c>apply: shadow</c>).
    /// </summary>
    public bool AsCandidate { get; set; }

    /// <summary>
    /// A new skill starts off.
    /// </summary>
    public bool StartsOff { get; set; }

    /// <summary>
    /// The technical account run settings answer to on this host.
    /// </summary>
    public string? ServiceAccount { get; set; }

    /// <summary>
    /// The version the store gave the part.
    /// </summary>
    public int? SavedVersion { get; set; }

    /// <summary>
    /// The SHA-256 of the part as the host has it after the import, for its link.
    /// </summary>
    public string? Hash { get; set; }

    /// <summary>
    /// The lockable fields of the part as the host has it after the import.
    /// </summary>
    public IReadOnlyDictionary<string, string>? Fields { get; set; }

    public bool Refused => Outcome == NhAssistantPackagePartOutcomes.Refused;

    /// <summary>
    /// Whether the import writes the part.
    /// </summary>
    public bool Writes => Outcome is NhAssistantPackagePartOutcomes.Created or NhAssistantPackagePartOutcomes.Changed;

    /// <summary>
    /// Whether the part is linked to the package after the import.
    /// </summary>
    public bool Linked => Outcome is NhAssistantPackagePartOutcomes.Created
        or NhAssistantPackagePartOutcomes.Changed
        or NhAssistantPackagePartOutcomes.Unchanged;

    /// <summary>
    /// Refuses the part with the code and field errors of a failed save.
    /// </summary>
    public void Refuse(TaskResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        Outcome = NhAssistantPackagePartOutcomes.Refused;
        var fields = NhAssistantResults.FieldErrorsOf(result);
        foreach (var (field, codes) in fields)
        {
            foreach (var code in codes)
            {
                Add(field, code);
            }
        }

        var failure = NhAssistantResults.CodeOf(result);
        if (failure is not null && (failure != NhAssistantAdminErrorCodes.ValidationFailed || fields.Count == 0))
        {
            Code ??= failure;
        }
    }

    /// <summary>
    /// Refuses the part with one field error.
    /// </summary>
    public void Refuse(string field, string error)
    {
        Outcome = NhAssistantPackagePartOutcomes.Refused;
        Add(field, error);
    }

    public NhAssistantAgentPackagePartDto ToDto()
    {
        return new NhAssistantAgentPackagePartDto(
            AgentId,
            Part,
            Outcome,
            ChangedLocally,
            Code,
            Errors.Count == 0 ? null : Errors.ToDictionary(item => item.Key, item => item.Value.ToArray(), StringComparer.Ordinal),
            LockedFields)
        {
            CandidateVersion = AsCandidate && SavedVersion is { } version ? version : null
        };
    }

    private void Add(string field, string error)
    {
        if (!Errors.TryGetValue(field, out var codes))
        {
            Errors[field] = codes = [];
        }
        if (!codes.Contains(error, StringComparer.Ordinal))
        {
            codes.Add(error);
        }
    }
}
