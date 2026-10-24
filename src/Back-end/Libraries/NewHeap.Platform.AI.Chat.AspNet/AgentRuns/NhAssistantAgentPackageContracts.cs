using System.Text.Json;
using System.Text.Json.Serialization;

namespace NewHeap.Platform.AI.Chat.AspNet.AgentRuns;

/// <summary>
/// A versioned agent package (S7-2): several agents with what belongs to each of them as data, read
/// strictly (<see cref="NhAssistantAgentPackageFormat"/>). It never holds history, candidates, runs,
/// feedback, run state, shadow overrides, MCP server definitions, the technical account or secrets.
/// References travel as names and an import checks them with the save validators.
/// </summary>
/// <param name="Format">Always <see cref="NhAssistantAgentPackageFormat.Name"/>.</param>
/// <param name="FormatVersion">The format's version, <see cref="NhAssistantAgentPackageFormat.Version"/>;
/// a newer one is refused with <c>assistant-package-format-unsupported</c>.</param>
/// <param name="Package">The package's identity: its id and version.</param>
/// <param name="Agents">The agents, 1 to <see cref="NhAssistantAgentPackageFormat.MaxAgents"/>, each id once.</param>
public sealed record NhAssistantAgentPackage(
    string Format,
    int FormatVersion,
    NhAssistantAgentPackageInfo Package,
    IReadOnlyList<NhAssistantPackageAgent> Agents);

/// <summary>
/// The identity of an agent package.
/// </summary>
/// <param name="Id">A dash-case id, at most 128 characters; by default the first agent's id.</param>
/// <param name="Version">1 or higher; an upgrade of a linked package has a higher version.</param>
/// <param name="Description">What the package is for, at most 500 characters.</param>
public sealed record NhAssistantAgentPackageInfo(string Id, int Version, string? Description = null);

/// <summary>
/// One agent in a package (PK1, PK2): an agent created in the administration travels whole, an agent
/// in code as an overlay of what data may change on it, and the import requires the same agent in code.
/// </summary>
/// <param name="Id">The agent's id, kept on import (PK5).</param>
/// <param name="Source"><c>admin</c> for an agent created in the administration, <c>code</c> for an
/// overlay of an agent in code (<see cref="NhAssistantAgentSources"/>).</param>
/// <param name="Definition">The definition of an agent created in the administration (required for
/// <c>admin</c>), or the overrides of an agent in code (part <c>agent</c>).</param>
/// <param name="RunSettings">The run settings of an agent created in the administration, without the
/// technical account (part <c>runSettings</c>); never for an agent in code.</param>
/// <param name="Skills">The live versions of its data skills.</param>
/// <param name="Triggers">The live versions of its data triggers; their ids stay.</param>
/// <param name="Consultations">Its consultations defined as data.</param>
/// <param name="HandOffs">The hand-offs defined as data that it accepts.</param>
/// <param name="Metadata">Its values of entries of agent metadata, by entry name; for an agent in code
/// only overrides of <c>Editable()</c> entries.</param>
/// <param name="Groups">The groups of agents it is in, by id.</param>
/// <param name="Locks">The locked fields per part key (<see cref="NhAssistantPackagePartKinds"/>), each
/// <c>*</c> or fields of <see cref="NhAssistantPackageLockFields"/>.</param>
/// <param name="Hashes">The SHA-256 of each part's canonical JSON, lowercase hex, by part key, as the
/// exporter computed them (<see cref="NhAssistantAgentPackageFormat.HashOf{T}"/>). Optional for a package a
/// tool writes; a hash that is given must match its part.</param>
public sealed record NhAssistantPackageAgent(
    string Id,
    string Source,
    NhAssistantPackageAgentDefinition? Definition = null,
    NhAssistantPackageRunSettings? RunSettings = null,
    IReadOnlyList<NhAssistantPackageSkill>? Skills = null,
    IReadOnlyList<NhAssistantPackageTrigger>? Triggers = null,
    IReadOnlyList<NhAssistantPackageConsultation>? Consultations = null,
    IReadOnlyList<NhAssistantPackageHandOff>? HandOffs = null,
    IReadOnlyDictionary<string, JsonElement>? Metadata = null,
    IReadOnlyList<string>? Groups = null,
    IReadOnlyDictionary<string, IReadOnlyList<string>>? Locks = null,
    IReadOnlyDictionary<string, string>? Hashes = null);

/// <summary>
/// The definition of an agent, mapped to <see cref="NhAssistantAgentRequest"/> on import. For an agent
/// in code only <see cref="DisplayName"/>, <see cref="Description"/>, <see cref="Instructions"/>,
/// <see cref="ToolSelectors"/>, <see cref="RequiredPolicy"/> and <see cref="Autonomy"/> apply, as the
/// overrides an administrator may make; <see langword="null"/> keeps the code's value.
/// </summary>
/// <param name="Autonomy"><c>suggest</c>, <c>assist</c> or another value of <c>NhAiAutonomyLevel</c> in
/// lowercase.</param>
/// <param name="StartsOff">For a new agent: it starts off.</param>
/// <param name="StartsInShadow">For a new agent: it starts in shadow.</param>
/// <param name="ShadowOf">The live agent a data shadow variant belongs to; another agent of the package
/// or one the host has.</param>
public sealed record NhAssistantPackageAgentDefinition(
    string? DisplayName = null,
    string? Description = null,
    string? Instructions = null,
    NhAssistantInstructionAssetReference? InstructionsAsset = null,
    IReadOnlyList<string>? ToolSelectors = null,
    string? RequiredPolicy = null,
    string? Autonomy = null,
    bool? OfferedInChat = null,
    IReadOnlyList<string>? WorksOnSubjectTypes = null,
    bool? WorksOnAllSubjects = null,
    bool? WorksOnApplication = null,
    bool? PerDivision = null,
    string? RunPolicy = null,
    string? ViewPolicy = null,
    string? Profile = null,
    string? DataClassification = null,
    bool? StartsOff = null,
    bool? StartsInShadow = null,
    string? ShadowOf = null);

/// <summary>
/// The run settings of an agent created in the administration, mapped to
/// <see cref="NhAssistantAgentRunSettingsRequest"/> on import. The technical account never travels:
/// the import names it per agent (<see cref="NhAssistantAgentPackageImportOptions.ServiceAccounts"/>).
/// </summary>
public sealed record NhAssistantPackageRunSettings(
    string Accountability,
    bool DelegateFromInitiator = false,
    bool AllowGatewayReads = false,
    string? ElevatedReads = null,
    string? ApprovalPolicy = null,
    int? RetryCount = null,
    int? DailyToolCalls = null,
    long? DailyTokens = null,
    int? MaxConcurrentRuns = null,
    int? MaxRunsPerSubjectPerDay = null,
    long? MaxTokensPerRun = null,
    string? TriageProfile = null,
    string? RuleHandler = null,
    bool AcceptsFiles = false,
    int? MaxFileBytes = null,
    int? MaxFilePages = null,
    int? MaxFilesPerTurn = null,
    int? MaxFileTokensPerTurn = null);

/// <summary>
/// The live version of a data skill, mapped to <see cref="NhAssistantDataSkillRequest"/> on import.
/// </summary>
/// <param name="StartsOff">For a new skill: it starts off.</param>
public sealed record NhAssistantPackageSkill(
    string Id,
    string Description,
    string? Instructions = null,
    NhAssistantInstructionAssetReference? InstructionsAsset = null,
    IReadOnlyList<string>? Tools = null,
    string? Profile = null,
    string? RuleHandler = null,
    long? MaxTokensPerRun = null,
    int? DailyToolCalls = null,
    long? DailyTokens = null,
    string? ApprovalPolicy = null,
    string? DataClassification = null,
    bool AcceptsFiles = false,
    int? MaxFileBytes = null,
    int? MaxFilePages = null,
    int? MaxFilesPerTurn = null,
    int? MaxFileTokensPerTurn = null,
    bool? StartsOff = null);

/// <summary>
/// The live version of a data trigger, mapped to <see cref="NhAssistantDataTriggerRequest"/> on
/// import. Its id stays (PK5): an id a trigger of an agent outside the package has is refused.
/// </summary>
/// <param name="Source"><c>event</c> or <c>schedule</c>.</param>
public sealed record NhAssistantPackageTrigger(
    string Id,
    string Source,
    string? Skill = null,
    string? Event = null,
    IReadOnlyList<string>? Filters = null,
    string? Cron = null,
    string? TimeZone = null,
    string? SubjectSet = null,
    string? Message = null,
    string? Conflict = null,
    int? DebounceSeconds = null);

/// <summary>
/// A consultation defined as data, mapped to <see cref="NhAssistantDataConsultationRequest"/> on import.
/// </summary>
/// <param name="StartsOff">For a new consultation: it starts off.</param>
public sealed record NhAssistantPackageConsultation(
    string SubjectType,
    string Description,
    string? Instructions = null,
    string? MinimumRole = null,
    string? AskerPolicy = null,
    IReadOnlyList<string>? Routes = null,
    string? ProposalRole = null,
    string? ProposalPolicy = null,
    IReadOnlyList<string>? ToolSelectors = null,
    int? MaxToolCalls = null,
    int? TimeoutSeconds = null,
    int? MaxAnswerChars = null,
    int? MaxHistoryMessages = null,
    bool AllowRequests = false,
    bool ExposeToMcp = false,
    string? DataClassification = null,
    bool? StartsOff = null);

/// <summary>
/// A hand-off defined as data that the agent accepts, mapped to <see cref="NhAssistantDataHandOffRequest"/>
/// on import. A source may be another agent of the package (PK17) or one the host has.
/// </summary>
public sealed record NhAssistantPackageHandOff(
    string SubjectType,
    IReadOnlyList<string> Sources,
    string? Description = null,
    bool NotifySource = false,
    int? MaxPerSubjectPerDay = null);

/// <summary>
/// How an export builds its package (PK13).
/// </summary>
/// <param name="PackageId">The package id; by default the id of the package the first agent is linked
/// to, otherwise the first agent's id. Exporting linked agents keeps their package id.</param>
/// <param name="Version">The package version; by default the next one after the linked package's,
/// otherwise 1.</param>
/// <param name="Description">What the package is for, at most 500 characters.</param>
/// <param name="Locks">Per agent id, the locked fields per part key, each <c>*</c> or fields of
/// <see cref="NhAssistantPackageLockFields"/>; a part the export does not hold is refused.</param>
public sealed record NhAssistantAgentPackageExportOptions(
    string? PackageId = null,
    int? Version = null,
    string? Description = null,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyList<string>>>? Locks = null);

/// <summary>
/// An exported package with the references its agents make to agents outside it (PK18).
/// </summary>
/// <param name="Package">The package, ready to download or import.</param>
/// <param name="Hash">The SHA-256 of the package's canonical JSON, lowercase hex.</param>
/// <param name="Warnings">Every reference to an agent outside the selection; that agent must exist on
/// the host the package is imported into.</param>
public sealed record NhAssistantAgentPackageExportDto(
    NhAssistantAgentPackage Package,
    string Hash,
    IReadOnlyList<NhAssistantAgentPackageWarningDto> Warnings);

/// <summary>
/// A reference of an exported agent to an agent outside the selection.
/// </summary>
/// <param name="AgentId">The exported agent.</param>
/// <param name="Part">The part that makes the reference, such as <c>handOff:project</c>.</param>
/// <param name="Code">What the reference is, one of <see cref="NhAssistantAgentPackageWarnings"/>.</param>
/// <param name="ReferencedAgentId">The agent outside the selection.</param>
public sealed record NhAssistantAgentPackageWarningDto(
    string AgentId,
    string Part,
    string Code,
    string ReferencedAgentId);

/// <summary>
/// The codes of <see cref="NhAssistantAgentPackageWarningDto.Code"/>.
/// </summary>
public static class NhAssistantAgentPackageWarnings
{
    /// <summary>A hand-off the agent accepts names a source outside the selection.</summary>
    public const string HandOffSource = "hand-off-source";

    /// <summary>The agent is a data shadow variant of a live agent outside the selection.</summary>
    public const string ShadowOf = "shadow-of";

    /// <summary>An agent outside the selection selects a consultation of the agent.</summary>
    public const string ConsultationAsker = "consultation-asker";

    /// <summary>A shadow variant outside the selection receives the agent's triggers.</summary>
    public const string ShadowVariant = "shadow-variant";
}

/// <summary>
/// How an import applies a package (PK4, PK7, PK10).
/// </summary>
/// <param name="ServiceAccounts">Per agent id, the technical account on this host for run settings that
/// answer to one; without it such run settings are refused with <c>serviceAccountActorId: required</c>.
/// The account must exist and may sign in (<c>assistant-service-account-not-found</c> otherwise).</param>
/// <param name="Apply"><c>live</c> (default): a changed skill or trigger becomes its new live version;
/// <c>shadow</c>: it becomes the candidate next to the live version, to compare and promote. New skills
/// and triggers start in shadow either way.</param>
/// <param name="Relink">Parts an administrator unlinked that take the package's version again instead of
/// staying <c>kept-local</c>; a reference without a part relinks every part of the agent, also an agent
/// that was unlinked as a whole.</param>
public sealed record NhAssistantAgentPackageImportOptions(
    IReadOnlyDictionary<string, string>? ServiceAccounts = null,
    string? Apply = null,
    IReadOnlyList<NhAssistantPackagePartReference>? Relink = null);

/// <summary>
/// A part of an agent: <paramref name="Part"/> a key of <see cref="NhAssistantPackagePartKinds"/>, or
/// <see langword="null"/> for every part of the agent.
/// </summary>
public sealed record NhAssistantPackagePartReference(string AgentId, string? Part = null);

/// <summary>
/// The outcome per part of a preview or an import (PK6), one of <see cref="NhAssistantPackagePartOutcomes"/>.
/// </summary>
/// <param name="AgentId">The agent.</param>
/// <param name="Part">The part key, such as <c>agent</c> or <c>skill:triage</c>.</param>
/// <param name="Outcome">What the import does or did with the part.</param>
/// <param name="ChangedLocally">Whether the host's part was changed after the last import of the
/// package: an import overwrites the unlocked fields that changed (PK10).</param>
/// <param name="Code">For a refused part without field errors: the failure code, such as
/// <c>assistant-agent-exists</c>; otherwise <see langword="null"/>.</param>
/// <param name="Errors">For a refused part: the field errors of the save validators, by field.</param>
/// <param name="LockedFields">The fields the package locks on the part, <c>*</c> for all.</param>
public sealed record NhAssistantAgentPackagePartDto(
    string AgentId,
    string Part,
    string Outcome,
    bool ChangedLocally,
    string? Code,
    IReadOnlyDictionary<string, string[]>? Errors,
    IReadOnlyList<string> LockedFields)
{
    /// <summary>
    /// For a skill or trigger an import makes a candidate of (<c>apply: shadow</c>): the candidate's
    /// version; otherwise <see langword="null"/>.
    /// </summary>
    public int? CandidateVersion { get; init; }
}

/// <summary>
/// The outcomes of <see cref="NhAssistantAgentPackagePartDto.Outcome"/>.
/// </summary>
public static class NhAssistantPackagePartOutcomes
{
    /// <summary>The host does not have the part; the import adds it.</summary>
    public const string Created = "created";

    /// <summary>The host has the part linked to the package and the package changes it.</summary>
    public const string Changed = "changed";

    /// <summary>The host has the part as the package has it.</summary>
    public const string Unchanged = "unchanged";

    /// <summary>An administrator unlinked the part; the import leaves it as it is (PK10).</summary>
    public const string KeptLocal = "kept-local";

    /// <summary>
    /// The host's agent, or part, came from an earlier version of the package that no longer contains
    /// it; the import unlinks it and never deletes it (PK5).
    /// </summary>
    public const string LeftBehind = "left-behind";

    /// <summary>The check refused the part; nothing of the package is imported.</summary>
    public const string Refused = "refused";
}

/// <summary>
/// What an import would do (<see cref="INhAssistantAgentPackages.PreviewImportAsync"/>) or did.
/// </summary>
/// <param name="PackageId">The package.</param>
/// <param name="PackageVersion">The package's version.</param>
/// <param name="InstalledVersion">The version of the package the host has; <see langword="null"/> for a
/// package it does not have.</param>
/// <param name="Importable">Whether no part is refused.</param>
/// <param name="Parts">Per agent, its parts in dependency order: agent, run settings, skills,
/// triggers, consultations, hand-offs and metadata; agents and parts left behind last.</param>
public sealed record NhAssistantAgentPackagePreviewDto(
    string PackageId,
    int PackageVersion,
    int? InstalledVersion,
    bool Importable,
    IReadOnlyList<NhAssistantAgentPackagePartDto> Parts);

/// <summary>
/// What an import did: every part's outcome, as the preview showed it.
/// </summary>
/// <param name="Hash">The SHA-256 of the package's canonical JSON, lowercase hex, as audited.</param>
public sealed record NhAssistantAgentPackageImportDto(
    string PackageId,
    int PackageVersion,
    string Hash,
    IReadOnlyList<NhAssistantAgentPackagePartDto> Parts);

/// <summary>
/// The parts an unlink took out of their package.
/// </summary>
/// <param name="PackageId">The package they belonged to.</param>
/// <param name="Parts">The part keys that are now local.</param>
public sealed record NhAssistantAgentPackageUnlinkDto(
    string AgentId,
    string PackageId,
    IReadOnlyList<string> Parts);

/// <summary>
/// The package link of one part (PK11), as the administration shows it next to the part: which
/// package it came from and which of its fields stay read-only.
/// </summary>
/// <param name="Part">The part key.</param>
/// <param name="PackageId">The package.</param>
/// <param name="PackageVersion">The version of the package the part was imported from.</param>
/// <param name="LockedFields">The locked fields, <c>*</c> for all; empty when the package locks none.</param>
/// <param name="Hash">The SHA-256 of the part's canonical JSON at import.</param>
/// <param name="ChangedLocally">Whether the part changed after its import; the next upgrade overwrites
/// its unlocked fields.</param>
/// <param name="LinkedAt">When the import linked it.</param>
/// <param name="LinkedBy">Who imported it.</param>
public sealed record NhAssistantPackagePartLinkDto(
    string Part,
    string PackageId,
    int PackageVersion,
    IReadOnlyList<string> LockedFields,
    string Hash,
    bool ChangedLocally,
    DateTimeOffset LinkedAt,
    string LinkedBy);

/// <summary>
/// The package an agent belongs to, with the link of every part that is still linked.
/// </summary>
/// <param name="PackageId">The package.</param>
/// <param name="PackageVersion">The version of the package the host has.</param>
/// <param name="Parts">The linked parts, ordered by part key.</param>
public sealed record NhAssistantAgentPackageLinkDto(
    string PackageId,
    int PackageVersion,
    IReadOnlyList<NhAssistantPackagePartLinkDto> Parts);

/// <summary>
/// The body of <c>POST admin/agent-packages/export</c>.
/// </summary>
/// <param name="AgentIds">The agents to export, 1 to 20; exactly these (PK18).</param>
public sealed record NhAssistantAgentPackageExportRequest(
    IReadOnlyList<string>? AgentIds,
    NhAssistantAgentPackageExportOptions? Options = null);

/// <summary>
/// The body of <c>POST admin/agent-packages/preview</c> and <c>POST admin/agent-packages/import</c>.
/// </summary>
/// <param name="Package">The package as JSON, read strictly (<see cref="NhAssistantAgentPackageFormat.Read(JsonElement)"/>).</param>
public sealed record NhAssistantAgentPackageImportRequest(
    JsonElement? Package,
    NhAssistantAgentPackageImportOptions? Options = null);

/// <summary>
/// The body of <c>POST admin/agents/{id}/package/unlink</c>.
/// </summary>
/// <param name="Part">The part key to unlink; <see langword="null"/> unlinks the whole agent.</param>
/// <param name="Reason">At most 500 characters.</param>
public sealed record NhAssistantAgentPackageUnlinkRequest(string? Part = null, string? Reason = null);

/// <summary>
/// The routes of the agent package endpoints, relative to the assistant's base path, all behind the
/// administration policy.
/// </summary>
public static class NhAssistantAgentPackageRoutes
{
    /// <summary>Exports agents as a package and answers with the JSON download.</summary>
    public const string Export = "admin/agent-packages/export";

    /// <summary>Previews an import per part.</summary>
    public const string Preview = "admin/agent-packages/preview";

    /// <summary>Imports a package in one transaction.</summary>
    public const string Import = "admin/agent-packages/import";

    /// <summary>Unlinks an agent, or one part of it, from its package.</summary>
    public const string Unlink = "admin/agents/{id}/package/unlink";

    /// <summary>
    /// The response header of the export download that lists the references to agents outside the
    /// selection (<see cref="NhAssistantAgentPackageExportDto.Warnings"/>) as a JSON array.
    /// </summary>
    public const string WarningsHeader = "X-NewHeap-Package-Warnings";
}

/// <summary>
/// The source-generated JSON of the agent package endpoints: web (camelCase) names, nulls written.
/// </summary>
[JsonSourceGenerationOptions(
    JsonSerializerDefaults.Web,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never)]
[JsonSerializable(typeof(NhAssistantAgentPackageExportRequest))]
[JsonSerializable(typeof(NhAssistantAgentPackageImportRequest))]
[JsonSerializable(typeof(NhAssistantAgentPackageUnlinkRequest))]
[JsonSerializable(typeof(NhAssistantAgentPackagePreviewDto))]
[JsonSerializable(typeof(NhAssistantAgentPackageImportDto))]
[JsonSerializable(typeof(NhAssistantAgentPackageUnlinkDto))]
[JsonSerializable(typeof(NhAssistantAgentPackageWarningDto[]))]
[JsonSerializable(typeof(NhAssistantAgentPackageLinkDto))]
public sealed partial class NhAssistantAgentPackageJsonSerializerContext : JsonSerializerContext;
