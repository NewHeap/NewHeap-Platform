namespace NewHeap.Platform.AI.Chat.Entities;

/// <summary>
/// An agent package this host imported (PK11): its id, the version in effect, the agents it holds on
/// this host and who imported it when. One row per package id; an upgrade replaces the version.
/// </summary>
public sealed class AssistantAgentPackage
{
    /// <summary>
    /// The package id, dash-case.
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// The version of the package the host has.
    /// </summary>
    public int Version { get; set; }

    /// <summary>
    /// The version of the package format it was read with.
    /// </summary>
    public int FormatVersion { get; set; }

    /// <summary>
    /// The SHA-256 of the package's canonical JSON at import, lowercase hex.
    /// </summary>
    public string Hash { get; set; } = string.Empty;

    /// <summary>
    /// The agents that belong to the package on this host, a JSON array of agent ids ordered by id.
    /// An agent an upgrade no longer contains, or that an administrator unlinked as a whole, is not in
    /// it.
    /// </summary>
    public string AgentIdsJson { get; set; } = "[]";

    public DateTimeOffset ImportedAt { get; set; }

    public string ImportedBy { get; set; } = string.Empty;

    public Guid ConcurrencyStamp { get; set; }
}

/// <summary>
/// The link of one part of an agent to the package it was imported from (PK11): the package and its
/// version, the hash of the part at import, and the fields the package locks with their values. One
/// row per agent, part kind and part key; it goes with its agent.
/// </summary>
public sealed class AssistantAgentPackageLink
{
    public string AgentId { get; set; } = string.Empty;

    /// <summary>
    /// One of <see cref="NhAssistantPackagePartKinds"/>.
    /// </summary>
    public string PartKind { get; set; } = string.Empty;

    /// <summary>
    /// The skill or trigger id, the subject type or the metadata entry's name; empty for the agent's
    /// definition and its run settings.
    /// </summary>
    public string PartKey { get; set; } = string.Empty;

    public string PackageId { get; set; } = string.Empty;

    public int PackageVersion { get; set; }

    /// <summary>
    /// The SHA-256 of the part's canonical JSON as the host had it right after the import, lowercase
    /// hex; a part whose hash differs now changed locally.
    /// </summary>
    public string Hash { get; set; } = string.Empty;

    /// <summary>
    /// The locked fields, a JSON array of field names or <c>["*"]</c>; <see langword="null"/> when the
    /// package locks none of the part.
    /// </summary>
    public string? LockedFieldsJson { get; set; }

    /// <summary>
    /// The package's value of every locked field as canonical JSON, by field: a save may keep the value
    /// it has or take the package's, never another. Instructions are named by their hash or asset,
    /// never by their text.
    /// </summary>
    public string? LockedValuesJson { get; set; }

    public DateTimeOffset LinkedAt { get; set; }

    public string LinkedBy { get; set; } = string.Empty;
}
