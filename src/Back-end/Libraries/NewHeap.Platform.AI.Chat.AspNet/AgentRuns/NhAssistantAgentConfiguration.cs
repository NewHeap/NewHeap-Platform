using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.Common.Actors;
using NewHeap.Platform.AI.AspNet;
using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.AI.Chat.Runs;
using NewHeap.Platform.Common.Models;

namespace NewHeap.Platform.AI.Chat.AspNet.AgentRuns;

/// <summary>
/// The run configuration of agents as data: skills, and the triggers that start them, defined at
/// runtime on agents whose runs are configured in code, and the run settings of agents created in
/// the administration, which then get skills and triggers the same way. Data names the building blocks code
/// registered (trigger events, subject sets, rule handlers, approval policies and model profiles)
/// and is checked against them on every save, as startup checks the code. A new skill and a new
/// trigger start in shadow; an administrator puts a skill live with
/// <c>INhAssistantAgentControl.SetSkillShadowAsync</c> and a trigger with
/// <see cref="SetTriggerShadowAsync"/>. Skills and triggers in code cannot change here. Every change
/// is audited and reaches every host within seconds. The administration endpoints under
/// <c>admin/agents/{id}</c> use it; applications call it as well, for example from a package import.
/// </summary>
public interface INhAssistantAgentConfiguration
{
    /// <summary>
    /// The building blocks data can name.
    /// </summary>
    NhAssistantRunCatalogDto GetCatalog();

    /// <summary>
    /// Creates an agent the way an administrator does in the administration, for example from a
    /// package import: offered in the chat unless <see cref="NhAssistantAgentRequest.OfferedInChat"/>
    /// is <see langword="false"/>, active unless <see cref="NhAssistantAgentRequest.StartsOff"/>
    /// starts it off, and without run settings until
    /// <see cref="SaveRunSettingsAsync"/> gives it some. An existing id fails with
    /// <c>assistant-agent-exists</c>; invalid input with field errors.
    /// </summary>
    Task<TaskResult<NhAssistantAgentConfigurationDto>> CreateAgentAsync(
        NhAssistantAgentRequest request,
        string actorId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The agent's run settings, skills and triggers, in code and as data, or <see langword="null"/>
    /// for an agent that does not exist or is registered in code without a run configuration.
    /// </summary>
    Task<NhAssistantAgentConfigurationDto?> GetAsync(string agentId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds or changes the run settings of an agent created in the administration: whom it answers
    /// to, its approval policy, budgets, run ceiling, retries, gateway reads, triage and rules. Grants
    /// are never data: the agent acts with the rights of the account it answers to, in its headless
    /// runs, within its tool selectors. An agent registered in code fails with
    /// <c>assistant-run-settings-in-code</c>; a technical account that does not exist or may not sign
    /// in fails with <c>assistant-service-account-not-found</c>. <c>expectedVersion</c> is the
    /// settings' current version, <see langword="null"/> for new settings.
    /// </summary>
    Task<TaskResult<NhAssistantAgentRunSettingsDto>> SaveRunSettingsAsync(
        string agentId,
        NhAssistantAgentRunSettingsRequest request,
        string actorId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes the run settings of an agent created in the administration, which then only chats.
    /// Fails with <c>assistant-run-settings-in-use</c> while it has data skills, data triggers or data
    /// consultations.
    /// </summary>
    Task<TaskResult> RemoveRunSettingsAsync(
        string agentId,
        int? expectedVersion,
        string actorId,
        string? reason = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Every version of a data skill, newest first; empty when the agent has no such data skill.
    /// </summary>
    Task<IReadOnlyList<NhAssistantDataSkillVersionDto>> GetSkillVersionsAsync(
        string agentId,
        string skillId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds a data skill, which starts in shadow, and off with <c>StartsOff</c>, or stores the next
    /// version of one. With
    /// <c>apply</c> <c>live</c> the version takes effect at once in the mode the skill has; with
    /// <c>shadow</c> it becomes the skill's candidate, which runs in the agent's shadow thread next to
    /// the live version on the triggers that name the skill, until it is promoted or discarded. A new
    /// version discards an earlier candidate. <c>expectedVersion</c> is the live version the change
    /// was made on, <see langword="null"/> for a new skill; otherwise the call fails with
    /// <c>assistant-version-conflict</c>.
    /// </summary>
    Task<TaskResult<NhAssistantDataSkillDto>> SaveSkillAsync(
        string agentId,
        string skillId,
        NhAssistantDataSkillRequest request,
        string actorId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Makes the candidate of <paramref name="candidateVersion"/> the live version of its skill; the
    /// live version stays as history. Runs that work in the candidate keep working; new runs use it
    /// live. Fails with <c>assistant-skill-candidate-not-found</c> without a candidate and with
    /// <c>assistant-version-conflict</c> when the candidate is another version.
    /// </summary>
    Task<TaskResult<NhAssistantDataSkillDto>> PromoteSkillCandidateAsync(
        string agentId,
        string skillId,
        int candidateVersion,
        string actorId,
        string? reason = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Discards the candidate of <paramref name="candidateVersion"/>; it stays as history and its
    /// queued runs end with <c>assistant-skill-version-gone</c>.
    /// </summary>
    Task<TaskResult> DiscardSkillCandidateAsync(
        string agentId,
        string skillId,
        int candidateVersion,
        string actorId,
        string? reason = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Compares the live version of a data skill with its candidate on the runs they made in the
    /// period: runs, outcomes, usage and proposals per tool, with what people decided about the live
    /// version's proposals, and how many events both handled. Counts only, no content.
    /// </summary>
    Task<TaskResult<NhAssistantSkillComparisonDto>> CompareSkillCandidateAsync(
        string agentId,
        string skillId,
        DateTimeOffset? from = null,
        DateTimeOffset? to = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Takes a data skill out of effect; its versions stay as history. Fails with
    /// <c>assistant-skill-in-use</c> while a data trigger names it. Runs that work in it end with
    /// <c>assistant-skill-not-found</c>.
    /// </summary>
    Task<TaskResult> RemoveSkillAsync(
        string agentId,
        string skillId,
        int? expectedVersion,
        string actorId,
        string? reason = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds a data trigger, which starts in shadow, or changes one in the mode it has. An event
    /// trigger may select named filters of its event (<see cref="NhAssistantDataTriggerRequest.Filters"/>);
    /// a name the event does not register fails with <c>assistant-trigger-filter-not-found</c>.
    /// </summary>
    Task<TaskResult<NhAssistantDataTriggerDto>> SaveTriggerAsync(
        string agentId,
        string triggerId,
        NhAssistantDataTriggerRequest request,
        string actorId,
        CancellationToken cancellationToken = default);

    Task<TaskResult> RemoveTriggerAsync(
        string agentId,
        string triggerId,
        int? expectedVersion,
        string actorId,
        string? reason = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Puts a data trigger in shadow (<see langword="true"/>) or live: in shadow its runs work in the
    /// agent's shadow thread of the subject, whatever the agent's and the skill's mode.
    /// </summary>
    Task<TaskResult<NhAssistantDataTriggerDto>> SetTriggerShadowAsync(
        string agentId,
        string triggerId,
        bool shadow,
        string actorId,
        string? reason = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds or changes the consultation defined as data of an agent about one subject type, within
    /// the ceiling in code (<c>UseConsultationsAsData</c>, and <c>ConsultableAsData</c> for an agent in
    /// code). It is in effect once saved; it answers when an asking agent selects its generated tool.
    /// An agent that may get none fails with <c>assistant-consultations-not-allowed</c>, an agent
    /// created in the administration without run settings with <c>assistant-agent-not-configured</c>,
    /// a subject type the agent allows consultation about in code with
    /// <c>assistant-consultation-in-code</c>, more than
    /// <see cref="NhAssistantAgentConfigurationLimits.MaxConsultationsPerAgent"/> with
    /// <c>assistant-configuration-limit</c>, a stale <c>expectedVersion</c> with
    /// <c>assistant-version-conflict</c> and invalid input with field errors, such as
    /// <c>routes: not-allowed</c> for a route outside the ceiling and <c>toolSelectors: not-allowed</c>
    /// for a tool selector the agent's own selectors do not cover.
    /// </summary>
    Task<TaskResult<NhAssistantDataConsultationDto>> SaveConsultationAsync(
        string agentId,
        string subjectType,
        NhAssistantDataConsultationRequest request,
        string actorId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes a consultation defined as data. Its tools disappear; a call of them fails with
    /// <c>assistant-consult-agent-unavailable</c>, while its recorded proposals can still be decided.
    /// Fails with <c>assistant-consultation-not-found</c> without one and with
    /// <c>assistant-consultation-in-code</c> for a consultation in code.
    /// </summary>
    Task<TaskResult> RemoveConsultationAsync(
        string agentId,
        string subjectType,
        int? expectedVersion,
        string actorId,
        string? reason = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Every entry of agent metadata registered with <c>AddAgentMetadata</c>, ordered by name, with the
    /// agent's value in effect, where it comes from and whether it changes here. Fails with
    /// <c>assistant-agent-not-found</c> for an agent that does not exist.
    /// </summary>
    Task<TaskResult<IReadOnlyList<NhAssistantAgentMetadataDto>>> GetMetadataAsync(
        string agentId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Stores the agent's value of one entry of agent metadata after the checks startup applies to
    /// values in code: the value fits the entry's type exactly (<c>value: invalid</c> otherwise), its
    /// size limit (<c>value: too-long</c>) and every validator (field errors <c>value</c> or
    /// <c>value.&lt;field&gt;</c> with the validator's codes). For an agent created at runtime it makes the
    /// agent's next version: <see cref="NhAssistantAgentMetadataRequest.ExpectedVersion"/> is the
    /// agent's version (required). For an agent in code only an entry registered with
    /// <c>Editable()</c> changes (<c>assistant-metadata-read-only</c> otherwise), as an override with a
    /// version of its own: <see langword="null"/> for a new override. A stale version fails with
    /// <c>assistant-version-conflict</c>, an entry that is not registered with
    /// <c>assistant-metadata-entry-not-found</c>. The audit names the entry, never the value.
    /// </summary>
    Task<TaskResult<NhAssistantAgentMetadataDto>> SaveMetadataAsync(
        string agentId,
        string name,
        NhAssistantAgentMetadataRequest request,
        string actorId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes the agent's stored value of one entry: of an agent created at runtime as its next
    /// version, of an agent in code its override, so the value in code applies again. Without a stored
    /// value nothing changes; otherwise <paramref name="expectedVersion"/> is required, as for
    /// <see cref="SaveMetadataAsync"/>. Returns the entry as it is now.
    /// </summary>
    Task<TaskResult<NhAssistantAgentMetadataDto>> RemoveMetadataAsync(
        string agentId,
        string name,
        int? expectedVersion,
        string actorId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// One entry of agent metadata of one agent, as the administration's slot for it shows it.
/// </summary>
/// <param name="Name">The entry's name, as registered with <c>AddAgentMetadata</c>.</param>
/// <param name="Editable">Whether the entry of this agent changes at runtime: always for an agent
/// created at runtime, for an agent in code only when the entry is registered with <c>Editable()</c>.</param>
/// <param name="MaxBytes">The largest value in bytes of its JSON.</param>
/// <param name="Source">Where the value in effect comes from: <c>code</c>, <c>override</c> or
/// <c>data</c>; <see langword="null"/> without a value.</param>
/// <param name="Value">The value in effect, in the shape of the entry's type; <see langword="null"/>
/// without one.</param>
/// <param name="Version">The version a change sends as <c>expectedVersion</c>: the agent's version for an
/// agent created at runtime, the override's version for an agent in code, <see langword="null"/> while
/// it has no override.</param>
/// <param name="AgentVersion">The version of the agent's definition; a change of an entry of an agent
/// created at runtime makes its next version.</param>
public sealed record NhAssistantAgentMetadataDto(
    string Name,
    bool Editable,
    int MaxBytes,
    string? Source,
    JsonElement? Value,
    int? Version,
    int AgentVersion)
{
    /// <summary>
    /// The value in code of an agent in code, next to an administrator's override; <see langword="null"/>
    /// without one.
    /// </summary>
    public JsonElement? CodeValue { get; init; }

    /// <summary>
    /// Whether the entry declares a searchable value with <c>IndexBy</c>.
    /// </summary>
    public bool Indexed { get; init; }

    /// <summary>
    /// The searchable value of the value in effect, trimmed and in lowercase.
    /// </summary>
    public string? IndexValue { get; init; }

    /// <summary>
    /// When the stored value was saved; <see langword="null"/> for a value in code or none.
    /// </summary>
    public DateTimeOffset? ChangedAt { get; init; }

    /// <summary>
    /// Who saved the stored value.
    /// </summary>
    public string? ChangedBy { get; init; }
}

/// <param name="Value">The value in the shape of the entry's type (camelCase JSON); required.</param>
/// <param name="ExpectedVersion">The <see cref="NhAssistantAgentMetadataDto.Version"/> the change was
/// made on: the agent's version for an agent created at runtime (required), the override's version for
/// an agent in code, <see langword="null"/> for a new override.</param>
public sealed record NhAssistantAgentMetadataRequest(JsonElement? Value, int? ExpectedVersion);

/// <param name="TriggerEvents">The events a data trigger can start a skill on.</param>
/// <param name="SubjectSets">The subjects a data schedule can start a skill for; a schedule without
/// one runs for the whole application.</param>
/// <param name="RuleHandlers">The rule handlers a data skill can work by.</param>
/// <param name="ApprovalPolicies">The ids of the registered approval policies.</param>
/// <param name="Profiles">The model profiles with chat, function calling and streaming.</param>
public sealed record NhAssistantRunCatalogDto(
    IReadOnlyList<NhAssistantRunBlockDto> TriggerEvents,
    IReadOnlyList<NhAssistantRunBlockDto> SubjectSets,
    IReadOnlyList<NhAssistantRunBlockDto> RuleHandlers,
    IReadOnlyList<string> ApprovalPolicies,
    IReadOnlyList<string> Profiles)
{
    /// <summary>
    /// Per approval policy, in the order of <see cref="ApprovalPolicies"/>: who decides, how long a
    /// proposal waits and how, so an administration can explain the choice.
    /// </summary>
    public IReadOnlyList<NhAssistantApprovalPolicySummaryDto> ApprovalPolicySummaries { get; init; } = [];

    /// <summary>
    /// Whether <c>GET admin/accounts</c> searches accounts by name: the application registered an
    /// <c>INhActorSearch</c>, as NewHeap Identity does.
    /// </summary>
    public bool AccountSearch { get; init; }

    /// <summary>
    /// The subject types the application registered with <c>AddSubjectType</c> and the platform's
    /// whole application (<c>nh-system</c>, with <see cref="NhAssistantSubjectTypeDto.IsSystem"/>),
    /// ordered by id, with their names in the request's language.
    /// </summary>
    public IReadOnlyList<NhAssistantSubjectTypeDto> SubjectTypes { get; init; } = [];

    /// <summary>
    /// The policies the application lists with <c>AddAgentPolicy</c>, ordered by name: what an
    /// administrator may choose as the run and view policy of an agent created in the administration.
    /// </summary>
    public IReadOnlyList<NhAssistantRunBlockDto> AgentPolicies { get; init; } = [];

    /// <summary>
    /// Whether the application lists its divisions with an <c>INhDivisionDirectory</c>, as NewHeap
    /// Identity does, so an agent may work per division; without it the administration does not offer
    /// the choice and everything runs app-wide.
    /// </summary>
    public bool Divisions { get; init; }

    /// <summary>
    /// The groups of agents the application registers with <c>AddAgentGroup</c>, ordered by id, with
    /// their names and descriptions in the request's language: what an administrator may put an agent
    /// in.
    /// </summary>
    public IReadOnlyList<NhAssistantAgentGroupDto> AgentGroups { get; init; } = [];

    /// <summary>
    /// The application's file limits (<see cref="NhAssistantLimits"/>): the run settings of an agent
    /// created in the administration and a data skill can only lower them.
    /// </summary>
    public NhAssistantFileLimitsDto? FileLimits { get; init; }

    /// <summary>
    /// The application's ceiling of consultations defined as data (<c>UseConsultationsAsData</c>);
    /// <see langword="null"/> while consultations exist in code only.
    /// </summary>
    public NhAssistantConsultationCeilingDto? ConsultationCeiling { get; init; }

    /// <summary>
    /// The entries of agent metadata the application registers with <c>AddAgentMetadata</c>, ordered by
    /// name: the slots of the agent editor, where the application shows its own editor per entry.
    /// </summary>
    public IReadOnlyList<NhAssistantAgentMetadataEntryDto> AgentMetadata { get; init; } = [];

    /// <summary>
    /// Whether the application registered a text asset source (<c>UseTextAssetSource</c>), so a data
    /// skill version and an agent created in the administration may take their instructions from its
    /// assets (<c>instructionsAsset</c>); <c>GET admin/instruction-assets</c> lists the ones it offers.
    /// </summary>
    public bool InstructionAssets { get; init; }
}

/// <summary>
/// The application's text assets an administrator may choose as instructions
/// (<c>GET admin/instruction-assets</c>).
/// </summary>
/// <param name="Supported">Whether the application registered a text asset source.</param>
/// <param name="Assets">The trusted system instructions it lists, in its order; empty when it lists
/// none, so an administrator names the id and version.</param>
public sealed record NhAssistantInstructionAssetListDto(
    bool Supported,
    IReadOnlyList<NhAssistantInstructionAssetDto> Assets);

/// <summary>
/// One asset of the application, without its content.
/// </summary>
/// <param name="AssetId">The asset's id.</param>
/// <param name="Version">The asset's version.</param>
/// <param name="Hash">The SHA-256 of its content as its manifest states it; a save checks it against
/// the content.</param>
public sealed record NhAssistantInstructionAssetDto(string AssetId, int Version, string Hash)
{
    /// <summary>
    /// The asset that replaces it, when the application deprecated it.
    /// </summary>
    public string? ReplacedByAssetId { get; init; }

    /// <summary>
    /// When the application deprecated it; <see langword="null"/> for an asset in use.
    /// </summary>
    public DateTimeOffset? DeprecatedAt { get; init; }
}

/// <summary>
/// The text of one asset of the application, for an administrator to read before choosing it
/// (<c>GET admin/instruction-assets/{id}/versions/{version}</c>).
/// </summary>
/// <param name="AssetId">The asset's id.</param>
/// <param name="Version">The asset's version.</param>
/// <param name="Hash">The SHA-256 of <paramref name="Content"/>, lowercase hex: the hash a reference
/// stores.</param>
/// <param name="Content">The asset's text, read-only.</param>
public sealed record NhAssistantInstructionAssetTextDto(string AssetId, int Version, string Hash, string Content)
{
    /// <summary>
    /// <c>asset-invalid</c> when the asset cannot be instructions (not trusted system instructions of
    /// the application, or a manifest whose hash is not the hash of its content), so a save refuses
    /// it; <see langword="null"/> otherwise.
    /// </summary>
    public string? Problem { get; init; }
}

/// <summary>
/// A ceiling in code of consultations defined as data.
/// </summary>
/// <param name="Routes">The routes of proposals data may use, values of
/// <see cref="NhAssistantConsultationRoutes"/>; empty allows answers only.</param>
/// <param name="AllowRequests">Data may generate the request tool.</param>
/// <param name="AllowMcp">MCP clients may see the tools of a consultation that asks for it.</param>
/// <param name="MaxToolCalls">The most tool calls of one consultation.</param>
/// <param name="MaxTimeoutSeconds">The longest consultation, in seconds.</param>
/// <param name="MaxAnswerChars">The longest answer.</param>
/// <param name="MaxHistoryMessages">The most thread messages a consulted agent reads.</param>
public sealed record NhAssistantConsultationCeilingDto(
    IReadOnlyList<string> Routes,
    bool AllowRequests,
    bool AllowMcp,
    int MaxToolCalls,
    int MaxTimeoutSeconds,
    int MaxAnswerChars,
    int MaxHistoryMessages)
{
    /// <summary>
    /// The most consultations defined as data of one agent.
    /// </summary>
    public int MaxConsultations { get; init; } = NhAssistantAgentConfigurationLimits.MaxConsultationsPerAgent;

    /// <summary>
    /// The longest instructions of a consultation defined as data.
    /// </summary>
    public int MaxInstructionsLength { get; init; } = NhAssistantAgentConfigurationLimits.MaxConsultationInstructionsLength;

    internal static NhAssistantConsultationCeilingDto? From(NhAssistantConsultationCeiling? ceiling)
    {
        return ceiling is null
            ? null
            : new NhAssistantConsultationCeilingDto(
                ceiling.Routes,
                ceiling.AllowRequests,
                ceiling.AllowMcp,
                ceiling.MaxToolCalls,
                (int)ceiling.MaxTimeout.TotalSeconds,
                ceiling.MaxAnswerChars,
                ceiling.MaxHistoryMessages);
    }
}

/// <summary>
/// A consultation registered in code with <c>AllowConsultation</c>, as the administration shows it
/// read-only.
/// </summary>
/// <param name="AllowedRoutes">The routes its registration allows, before the agent's route switches.</param>
/// <param name="Exposure">Where its tools are offered: <c>local</c>, <c>agent</c>, <c>mcp</c>.</param>
public sealed record NhAssistantCodeConsultationDto(
    string SubjectType,
    string? Description,
    IReadOnlyList<string> AllowedRoutes,
    bool AllowRequests,
    IReadOnlyList<string> Exposure)
{
    /// <summary>
    /// The id of the generated consult tool, which asking agents select.
    /// </summary>
    public string ConsultToolId { get; init; } = string.Empty;

    /// <summary>
    /// The id of the generated request tool; <see langword="null"/> without requests.
    /// </summary>
    public string? RequestToolId { get; init; }

    /// <summary>
    /// The agents whose tool selectors select the consult tool now, ordered by id.
    /// </summary>
    public IReadOnlyList<string> AskingAgents { get; init; } = [];

    /// <summary>
    /// The tools the consulted agent is offered, from <c>Tools(...)</c>; <see langword="null"/> offers
    /// every tool of the agent.
    /// </summary>
    public IReadOnlyList<string>? ToolSelectors { get; init; }
}

/// <summary>
/// A consultation defined as data, as stored, with what is in effect of it.
/// </summary>
/// <param name="MinimumRole">The role the asking person needs on the subject: <c>reader</c>,
/// <c>participant</c> or <c>approver</c>.</param>
/// <param name="AskerPolicy">The policy the asking person must pass, one listed with
/// <c>AddAgentPolicy</c>; <see langword="null"/> uses the agent's required policy, else the access
/// policy.</param>
/// <param name="Routes">The routes as saved; empty for a consultation that only answers.</param>
/// <param name="ProposalRole">The role the asking person needs before the agent may propose.</param>
/// <param name="ProposalPolicy">The approval policy of its proposals; <see langword="null"/> uses the
/// agent's.</param>
/// <param name="DataClassification">The classification of its answers; <see langword="null"/> uses the
/// agent's.</param>
/// <param name="InEffect">Whether the consultation is in effect; <see langword="false"/> with
/// <paramref name="Problem"/> when it no longer fits the code.</param>
/// <param name="Problem">The first problem, the reason it is out of effect when it is.</param>
public sealed record NhAssistantDataConsultationDto(
    string SubjectType,
    int Version,
    string Description,
    string? Instructions,
    string? InstructionsHash,
    string MinimumRole,
    string? AskerPolicy,
    IReadOnlyList<string> Routes,
    string ProposalRole,
    string? ProposalPolicy,
    int MaxToolCalls,
    int TimeoutSeconds,
    int MaxAnswerChars,
    int MaxHistoryMessages,
    bool AllowRequests,
    bool ExposeToMcp,
    string? DataClassification,
    DateTimeOffset ChangedAt,
    string ChangedBy,
    string? Reason,
    bool InEffect,
    string? Problem)
{
    /// <summary>
    /// Every problem of the consultation, values of <see cref="NhAssistantRunConfigurationProblems"/>:
    /// the reason it is out of effect, or each part that no longer fits and was dropped or capped.
    /// </summary>
    public IReadOnlyList<string> Problems { get; init; } = [];

    /// <summary>
    /// The routes in effect: those saved that still fit the ceiling and the agent, before the agent's
    /// route switches.
    /// </summary>
    public IReadOnlyList<string> RoutesInEffect { get; init; } = [];

    /// <summary>
    /// The tool selection as saved; <see langword="null"/> offers every tool of the agent.
    /// </summary>
    public IReadOnlyList<string>? ToolSelectors { get; init; }

    /// <summary>
    /// The tool selection in effect: the saved selectors the agent's own selectors still cover.
    /// <see langword="null"/> offers every tool of the agent; empty offers none, when every selector
    /// dropped with the problem <c>tools-not-allowed</c>, or while the consultation is out of effect.
    /// </summary>
    public IReadOnlyList<string>? ToolSelectorsInEffect { get; init; }

    /// <summary>
    /// The id of the generated consult tool, which asking agents select.
    /// </summary>
    public string ConsultToolId { get; init; } = string.Empty;

    /// <summary>
    /// The id of the generated request tool when requests are in effect.
    /// </summary>
    public string? RequestToolId { get; init; }

    /// <summary>
    /// The agents whose tool selectors select the consult tool now, ordered by id: the second key of
    /// the consultation (AC5).
    /// </summary>
    public IReadOnlyList<string> AskingAgents { get; init; } = [];

    public DateTimeOffset CreatedAt { get; init; }

    public string CreatedBy { get; init; } = string.Empty;
}

/// <summary>
/// A consultation to define as data with <see cref="INhAssistantAgentConfiguration.SaveConsultationAsync"/>.
/// </summary>
/// <param name="Description">What the asking model reads about the agent, required, at most 1,000
/// characters.</param>
/// <param name="Instructions">Extra instructions of the consulted agent, at most 4,000 characters.</param>
/// <param name="MinimumRole">The role the asking person needs on the subject: <c>reader</c> (default),
/// <c>participant</c> or <c>approver</c>.</param>
/// <param name="AskerPolicy">A policy the application lists with <c>AddAgentPolicy</c> that the asking
/// person must pass; without it the agent's required policy, else the access policy.</param>
/// <param name="Routes">How the agent's proposals are decided: <c>asker-decides</c>, and at most one of
/// <c>subject-asker-rights</c> and <c>subject-agent-rights</c>, within the ceiling; empty or
/// <see langword="null"/> keeps the consultation read-only.</param>
/// <param name="ProposalRole">The role the asking person needs before the agent may propose:
/// <c>participant</c> (default), <c>reader</c> or <c>approver</c>.</param>
/// <param name="ProposalPolicy">An approval policy registered with <c>AddApprovalPolicy</c>.</param>
/// <param name="MaxToolCalls">0 to 32, at most the ceiling's; 4 by default, capped by the ceiling.
/// The other limits follow the same rule.</param>
/// <param name="TimeoutSeconds">1 to 600; 60 by default.</param>
/// <param name="MaxAnswerChars">200 to 32,000; 4,000 by default.</param>
/// <param name="MaxHistoryMessages">0 to 200; 20 by default.</param>
/// <param name="AllowRequests">Also generates the request tool, when the ceiling allows requests.</param>
/// <param name="ExposeToMcp">MCP clients may see its tools, when the ceiling allows MCP.</param>
/// <param name="DataClassification">The classification of its answers: <c>public</c>,
/// <c>internal</c>, <c>confidential</c> or <c>restricted</c>; <see langword="null"/> uses the agent's.</param>
/// <param name="ExpectedVersion">The consultation's version the change was made on;
/// <see langword="null"/> for a new consultation.</param>
/// <param name="Reason">At most 500 characters.</param>
public sealed record NhAssistantDataConsultationRequest(
    string? Description,
    string? Instructions = null,
    string? MinimumRole = null,
    string? AskerPolicy = null,
    IReadOnlyList<string>? Routes = null,
    string? ProposalRole = null,
    string? ProposalPolicy = null,
    int? MaxToolCalls = null,
    int? TimeoutSeconds = null,
    int? MaxAnswerChars = null,
    int? MaxHistoryMessages = null,
    bool AllowRequests = false,
    bool ExposeToMcp = false,
    string? DataClassification = null,
    int? ExpectedVersion = null,
    string? Reason = null)
{
    /// <summary>
    /// The tools the consulted agent is offered, by id or <c>prefix.*</c> selector, each covered by the
    /// agent's own selectors (field error <c>toolSelectors: not-allowed</c> otherwise,
    /// <c>toolSelectors: invalid</c> for a selector that is not one); <see langword="null"/> or empty
    /// offers every tool of the agent.
    /// </summary>
    public IReadOnlyList<string>? ToolSelectors { get; init; }
}

/// <summary>
/// File limits: the largest file in bytes, the most pages of one file, the most files of one turn and
/// the most estimated file tokens of one turn.
/// </summary>
public sealed record NhAssistantFileLimitsDto(
    int MaxFileBytes,
    int MaxFilePages,
    int MaxFilesPerTurn,
    int MaxFileTokensPerTurn);

/// <summary>
/// A subject type as people see it.
/// </summary>
/// <param name="Id">The dash-case type id, such as <c>project</c>.</param>
/// <param name="Name">The name of one subject in the request's language or the fallback language;
/// the id when the application did not name the type.</param>
/// <param name="PluralName">The name of several subjects, when the application gave one.</param>
/// <param name="EntityType">The short type name of the entity the type stands for, from
/// <c>ForEntity</c>, as the NewHeap log records <c>ObjectType</c>.</param>
/// <param name="EntityTypeFull">The full type name of that entity, as <c>ObjectTypeFull</c>.</param>
/// <param name="Link">The link template with <c>{id}</c>, from <c>OpenAt</c>.</param>
/// <param name="Searchable">Whether <c>GET subjects/{type}/search</c> finds subjects by name; without
/// it people give a subject's id.</param>
/// <param name="IsSystem">Whether the type belongs to the platform instead of the application.</param>
public sealed record NhAssistantSubjectTypeDto(
    string Id,
    string Name,
    string? PluralName,
    string? EntityType,
    string? EntityTypeFull,
    string? Link,
    bool Searchable,
    bool IsSystem);

/// <param name="Name">The policy id.</param>
/// <param name="Approvers">Who may decide; any of them can.</param>
/// <param name="LifetimeMinutes">How long a proposal waits for a decision.</param>
/// <param name="FourEyes">Whether the person who caused a proposal may not decide it.</param>
/// <param name="Mode"><c>inline</c>: the run waits for the decision; <c>deferred</c>: the run ends and
/// people decide later.</param>
public sealed record NhAssistantApprovalPolicySummaryDto(
    string Name,
    IReadOnlyList<NhAssistantApproverSummaryDto> Approvers,
    int LifetimeMinutes,
    bool FourEyes,
    string Mode);

/// <param name="Kind"><c>initiator</c> (the person who caused the run), <c>subject-role</c> or
/// <c>policy</c> (an authorization policy).</param>
/// <param name="Role">For <c>subject-role</c>: at least <c>reader</c>, <c>participant</c> or
/// <c>approver</c> on the subject.</param>
/// <param name="NamedRole">For <c>subject-role</c>: the application's role name, when one is required.</param>
/// <param name="Policy">For <c>policy</c>: the authorization policy.</param>
public sealed record NhAssistantApproverSummaryDto(
    string Kind,
    string? Role = null,
    string? NamedRole = null,
    string? Policy = null);

/// <param name="ActorId">The account's actor id, as run settings store it.</param>
/// <param name="DisplayName">The name people see.</param>
/// <param name="Detail">The e-mail address, when the application knows it.</param>
public sealed record NhAssistantAccountDto(string ActorId, string DisplayName, string? Detail);

/// <summary>
/// An agent to create with <see cref="INhAssistantAgentConfiguration.CreateAgentAsync"/>. Its tool
/// selectors bound what its skills and runs may use.
/// </summary>
/// <param name="WorksOnSubjectTypes">The registered object types the agent works on, at most 20.</param>
/// <param name="WorksOnAllSubjects">The agent works on every object type; not next to
/// <paramref name="WorksOnSubjectTypes"/>.</param>
/// <param name="WorksOnApplication">The agent works on the whole application.</param>
/// <param name="PerDivision">The agent works per division; only when the application lists its
/// divisions (<see cref="NhAssistantRunCatalogDto.Divisions"/>).</param>
/// <param name="RunPolicy">Who may run the agent for the whole application: a policy listed with
/// <c>AddAgentPolicy</c>; without it the administration policy.</param>
/// <param name="ViewPolicy">Who sees its results for the whole application: a policy listed with
/// <c>AddAgentPolicy</c>; without it the agent's required policy.</param>
/// <param name="StartsOff">The agent starts off: its triggers are ignored until someone switches it on
/// with <c>INhAssistantAgentControl.ResumeAsync</c>. People can still chat with it and start it.</param>
/// <param name="Profile">The model profile of its model calls, one of the run catalog's
/// <see cref="NhAssistantRunCatalogDto.Profiles"/> (field error <c>profile: not-found</c> otherwise);
/// without it the application's chat profile.</param>
/// <param name="DataClassification">The classification of the data its model calls carry, in people's
/// chats with it and in its runs: <c>public</c>, <c>internal</c>, <c>confidential</c> or
/// <c>restricted</c> (field error <c>dataClassification: invalid</c> otherwise); without it
/// <c>internal</c>. A skill's own wins.</param>
/// <remarks>
/// Without <paramref name="WorksOnSubjectTypes"/>, <paramref name="WorksOnAllSubjects"/> and
/// <paramref name="WorksOnApplication"/> the agent works on all object types and not on the
/// application.
/// </remarks>
public sealed record NhAssistantAgentRequest(
    string Id,
    string DisplayName,
    string Description,
    string Instructions,
    IReadOnlyList<string> ToolSelectors,
    NhAiAutonomyLevel Autonomy,
    string? RequiredPolicy = null,
    bool OfferedInChat = true,
    IReadOnlyList<string>? WorksOnSubjectTypes = null,
    bool? WorksOnAllSubjects = null,
    bool? WorksOnApplication = null,
    bool PerDivision = false,
    string? RunPolicy = null,
    string? ViewPolicy = null,
    bool StartsOff = false,
    string? Profile = null,
    string? DataClassification = null)
{
    /// <summary>
    /// Values of entries of agent metadata registered with <c>AddAgentMetadata</c>, by entry name, in
    /// the shape of each entry's type; stored with the agent's first version after the checks of
    /// <see cref="INhAssistantAgentConfiguration.SaveMetadataAsync"/>, as field errors
    /// <c>metadata.&lt;name&gt;</c> (<c>not-found</c> for an entry that is not registered).
    /// </summary>
    public IReadOnlyDictionary<string, JsonElement>? Metadata { get; init; }

    /// <summary>
    /// The asset of the application the agent takes its instructions from, instead of
    /// <see cref="Instructions"/>, which is then empty; checked as
    /// <see cref="NhAssistantDataSkillRequest.InstructionsAsset"/> is.
    /// </summary>
    public NhAssistantInstructionAssetReference? InstructionsAsset { get; init; }

    /// <summary>
    /// The live agent the new agent is a data shadow variant of, within <c>UseShadowVariantsAsData</c>:
    /// it receives every trigger of the live agent once it has run settings, always works in shadow,
    /// is never offered in the chat and never changes its live agent. Fails with
    /// <c>assistant-shadow-variants-not-allowed</c> outside the ceiling,
    /// <c>assistant-configuration-limit</c> beyond its number of variants, and field errors
    /// <c>shadowOf: not-found</c> for a live agent without a run configuration and
    /// <c>shadowOf: invalid</c> for one that is no live agent.
    /// </summary>
    public string? ShadowOf { get; init; }
}

/// <param name="Name">The building block's name.</param>
/// <param name="Description">What it means, for the administration.</param>
public sealed record NhAssistantRunBlockDto(string Name, string Description)
{
    /// <summary>
    /// For a trigger event that names its subject type up front, that type; data triggers on it fit
    /// only agents that work on it. <see langword="null"/> when the type is known only when it fires.
    /// </summary>
    public string? SubjectType { get; init; }

    /// <summary>
    /// For a trigger event: the named filters a data trigger on it may select, each with its name and
    /// description, in the order the application registered them; empty when it has none.
    /// <see langword="null"/> for the other building blocks.
    /// </summary>
    public IReadOnlyList<NhAssistantRunBlockDto>? Filters { get; init; }
}

/// <param name="ToolSelectors">The agent's own tool selectors; a data skill's tools stay within
/// them.</param>
/// <param name="Problems">Data that is out of effect because it no longer fits the code.</param>
/// <param name="Source"><c>code</c> for an agent registered in code, <c>admin</c> for one created in
/// the administration.</param>
/// <param name="RunSettings">The run settings of an agent created in the administration;
/// <see langword="null"/> for an agent in code and for one that only chats.</param>
public sealed record NhAssistantAgentConfigurationDto(
    string AgentId,
    IReadOnlyList<string> ToolSelectors,
    IReadOnlyList<NhAssistantCodeSkillDto> CodeSkills,
    IReadOnlyList<NhAssistantDataSkillDto> DataSkills,
    IReadOnlyList<NhAssistantCodeTriggerDto> CodeTriggers,
    IReadOnlyList<NhAssistantDataTriggerDto> DataTriggers,
    IReadOnlyList<NhAssistantRunConfigurationProblem> Problems,
    string Source,
    NhAssistantAgentRunSettingsDto? RunSettings)
{
    /// <summary>
    /// How an agent in code runs, as its code says, for an overview next to its skills and triggers;
    /// <see langword="null"/> for an agent created in the administration, whose
    /// <see cref="RunSettings"/> are data.
    /// </summary>
    public NhAssistantCodeRunSettingsDto? CodeRunSettings { get; init; }

    /// <summary>
    /// The consultations the agent allows in code, read-only, ordered by subject type.
    /// </summary>
    public IReadOnlyList<NhAssistantCodeConsultationDto> CodeConsultations { get; init; } = [];

    /// <summary>
    /// The consultations of the agent defined as data, ordered by subject type.
    /// </summary>
    public IReadOnlyList<NhAssistantDataConsultationDto> DataConsultations { get; init; } = [];

    /// <summary>
    /// The ceiling within which the agent may get consultations as data; <see langword="null"/> when
    /// it may get none.
    /// </summary>
    public NhAssistantConsultationCeilingDto? ConsultationCeiling { get; init; }
}

/// <summary>
/// The run settings of an agent in code that an administration shows; they change only in code.
/// The technical account, which an administrator may replace at runtime, is part of the agent's
/// state instead.
/// </summary>
/// <param name="Accountability"><c>agent</c>, <c>subject-responsible</c> or
/// <c>service-account</c>: whose rights its runs use.</param>
/// <param name="DelegateFromInitiator">Its runs also use what the person who started them may
/// use.</param>
/// <param name="ApprovalPolicy">The agent's approval policy, used when no tool or trigger policy is
/// more specific; <see langword="null"/> for the platform's default.</param>
/// <param name="UsesRules">The agent works by a rule handler in code instead of the model.</param>
public sealed record NhAssistantCodeRunSettingsDto(
    string Accountability,
    bool DelegateFromInitiator,
    string? ApprovalPolicy,
    bool UsesRules)
{
    /// <summary>
    /// The agent starts off in code (<c>StartsOff()</c>): it was off when the application first saw
    /// it. Its state now is in the agent's state.
    /// </summary>
    public bool StartsOff { get; init; }

    /// <summary>
    /// The agent accepts files in code (<c>AcceptsFiles()</c>).
    /// </summary>
    public bool AcceptsFiles { get; init; }
}

/// <param name="Accountability"><c>agent</c>, <c>subject-responsible</c> or
/// <c>service-account</c>.</param>
/// <param name="ElevatedReads"><c>restricted</c> or <c>deny</c>.</param>
/// <param name="InEffect">Whether the settings are in effect; <see langword="false"/> with
/// <paramref name="Problem"/> when they no longer fit the code.</param>
public sealed record NhAssistantAgentRunSettingsDto(
    int Version,
    string Accountability,
    string? ServiceAccountActorId,
    bool DelegateFromInitiator,
    bool AllowGatewayReads,
    string ElevatedReads,
    string? ApprovalPolicy,
    int RetryCount,
    int? DailyToolCalls,
    long? DailyTokens,
    int MaxConcurrentRuns,
    int MaxRunsPerSubjectPerDay,
    long? MaxTokensPerRun,
    string? TriageProfile,
    string? RuleHandler,
    DateTimeOffset ChangedAt,
    string ChangedBy,
    string? Reason,
    bool InEffect,
    string? Problem)
{
    /// <summary>
    /// Whether people's messages and the agent's tools may give it files.
    /// </summary>
    public bool AcceptsFiles { get; init; }

    /// <summary>
    /// The largest file in bytes the agent reads; <see langword="null"/> keeps the application's.
    /// </summary>
    public int? MaxFileBytes { get; init; }

    /// <summary>
    /// The most pages of one file; <see langword="null"/> keeps the application's.
    /// </summary>
    public int? MaxFilePages { get; init; }

    /// <summary>
    /// The most files of one turn; <see langword="null"/> keeps the application's.
    /// </summary>
    public int? MaxFilesPerTurn { get; init; }

    /// <summary>
    /// The most estimated file tokens of one turn; <see langword="null"/> keeps the application's.
    /// </summary>
    public int? MaxFileTokensPerTurn { get; init; }
}

/// <param name="Accountability"><c>agent</c>, <c>subject-responsible</c> or <c>service-account</c>.
/// The agent acts with the rights of the person or technical account it answers to; one that answers
/// to itself can only use what the person who starts it may use, with
/// <paramref name="DelegateFromInitiator"/>.</param>
/// <param name="ServiceAccountActorId">The technical account, at most 256 characters, for
/// <c>service-account</c> only.</param>
/// <param name="DelegateFromInitiator">The agent also uses what the person who started a run may
/// use.</param>
/// <param name="AllowGatewayReads">Headless runs read through the API bridge gateway with the
/// accountable person's rights; not for an agent that answers to itself.</param>
/// <param name="ElevatedReads"><c>restricted</c> (default) shields reads with the accountable
/// account's rights from thread readers without the tool's policies; <c>deny</c> refuses them.</param>
/// <param name="ApprovalPolicy">A policy registered with <c>AddApprovalPolicy</c>.</param>
/// <param name="RetryCount">Automatic retries after a transient failure, 0 to 10; 3 by default.</param>
/// <param name="DailyToolCalls">With <paramref name="DailyTokens"/>, the daily budget per scope.</param>
/// <param name="MaxConcurrentRuns">1 to 100; 4 by default.</param>
/// <param name="MaxRunsPerSubjectPerDay">1 to 1,000; 10 by default.</param>
/// <param name="MaxTokensPerRun">The run ceiling in tokens.</param>
/// <param name="TriageProfile">A chat profile that chooses the skill of a run no trigger or rule
/// routed.</param>
/// <param name="RuleHandler">The name of a rule handler registered with <c>AddRuleHandler</c>.</param>
/// <param name="ExpectedVersion">The settings' current version; <see langword="null"/> for new
/// settings.</param>
/// <param name="Reason">At most 500 characters.</param>
/// <param name="AcceptsFiles">People's messages and the agent's tools may give it files, within the
/// application's limits; <see langword="false"/> (default) refuses them.</param>
/// <param name="MaxFileBytes">With <paramref name="AcceptsFiles"/>: the largest file in bytes, from 1 to
/// the application's <see cref="NhAssistantLimits.MaxFileBytes"/>; <see langword="null"/> keeps the
/// application's. The other file limits follow the same rule.</param>
/// <param name="MaxFilePages">The most pages of one file, at most the application's.</param>
/// <param name="MaxFilesPerTurn">The most files of one turn, at most the application's.</param>
/// <param name="MaxFileTokensPerTurn">The most estimated file tokens of one turn, at most the
/// application's.</param>
public sealed record NhAssistantAgentRunSettingsRequest(
    string? Accountability,
    string? ServiceAccountActorId = null,
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
    int? ExpectedVersion = null,
    string? Reason = null,
    bool AcceptsFiles = false,
    int? MaxFileBytes = null,
    int? MaxFilePages = null,
    int? MaxFilesPerTurn = null,
    int? MaxFileTokensPerTurn = null);

public sealed record NhAssistantCodeSkillDto(string SkillId, string Description)
{
    /// <summary>
    /// The skill starts off in code (<c>StartsOff()</c>): it was off when the application first saw
    /// it. Its state now is in the skill's state.
    /// </summary>
    public bool StartsOff { get; init; }

    /// <summary>
    /// The skill accepts files in code (<c>AcceptsFiles()</c>).
    /// </summary>
    public bool AcceptsFiles { get; init; }
}

/// <param name="Source"><c>event</c>, <c>schedule</c> or <c>webhook</c>.</param>
public sealed record NhAssistantCodeTriggerDto(string TriggerId, string Source, string? SkillId)
{
    /// <summary>
    /// The subject type the trigger names up front: an object type such as <c>project</c>, or
    /// <c>nh-system</c> for a schedule that runs for the whole application (once per division for an
    /// agent that works per division). <see langword="null"/> when the type is known only when it
    /// fires, such as a schedule over subjects or an event whose subject is chosen by a function.
    /// </summary>
    public string? SubjectType { get; init; }
}

/// <param name="InEffect">Whether the live version is in effect; <see langword="false"/> with
/// <paramref name="Problem"/> when it no longer fits the code.</param>
public sealed record NhAssistantDataSkillDto(
    string SkillId,
    int Version,
    string Description,
    string? Instructions,
    string? InstructionsHash,
    IReadOnlyList<string> Tools,
    string? Profile,
    string? RuleHandler,
    long? MaxTokensPerRun,
    int? DailyToolCalls,
    long? DailyTokens,
    string? ApprovalPolicy,
    DateTimeOffset ChangedAt,
    string ChangedBy,
    string? Reason,
    bool InEffect,
    string? Problem)
{
    /// <summary>
    /// The candidate version that runs in shadow next to this live version; <see langword="null"/>
    /// without one.
    /// </summary>
    public NhAssistantDataSkillDto? Candidate { get; init; }

    /// <summary>
    /// The classification of the data the skill's model calls carry, one of
    /// <see cref="NhAssistantDataClassificationNames"/>; <see langword="null"/> uses the agent's.
    /// </summary>
    public string? DataClassification { get; init; }

    /// <summary>
    /// Whether a run in the skill reads files, when its agent accepts them too.
    /// </summary>
    public bool AcceptsFiles { get; init; }

    /// <summary>
    /// Why the skill, saved and in effect, may still not work as configured, as
    /// <see cref="NhAssistantConfigurationWarnings"/>:
    /// <see cref="NhAssistantConfigurationWarnings.ClassificationNotPermitted"/> when neither its
    /// profile, or else its agent's, nor any of the profile's fallbacks permits its classification, or
    /// else its agent's. Empty without warnings. A warning never refuses a save.
    /// </summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <summary>
    /// The largest file in bytes a run in the skill reads; <see langword="null"/> keeps the agent's.
    /// </summary>
    public int? MaxFileBytes { get; init; }

    /// <summary>
    /// The most pages of one file; <see langword="null"/> keeps the agent's.
    /// </summary>
    public int? MaxFilePages { get; init; }

    /// <summary>
    /// The most files of one turn; <see langword="null"/> keeps the agent's.
    /// </summary>
    public int? MaxFilesPerTurn { get; init; }

    /// <summary>
    /// The most estimated file tokens of one turn; <see langword="null"/> keeps the agent's.
    /// </summary>
    public int? MaxFileTokensPerTurn { get; init; }

    /// <summary>
    /// The asset of the application the version takes its instructions from; <see langword="null"/>
    /// for inline <see cref="Instructions"/> or none. Read its text with
    /// <c>GET admin/instruction-assets/{id}/versions/{version}</c>; <see cref="Problem"/> says when it no
    /// longer resolves to <see cref="InstructionsHash"/>.
    /// </summary>
    public NhAssistantInstructionAssetReference? InstructionsAsset { get; init; }
}

/// <param name="Version">The candidate version the change is for.</param>
/// <param name="Reason">At most 500 characters.</param>
public sealed record NhAssistantSkillCandidateRequest(int? Version, string? Reason = null);

/// <param name="PairedEvents">The events both versions handled.</param>
public sealed record NhAssistantSkillComparisonDto(
    string AgentId,
    string SkillId,
    DateTimeOffset From,
    DateTimeOffset To,
    NhAssistantSkillVersionStatsDto Live,
    NhAssistantSkillVersionStatsDto Candidate,
    int PairedEvents,
    IReadOnlyList<NhAssistantSkillComparisonToolDto> Tools);

/// <param name="Version">The version, live or candidate.</param>
public sealed record NhAssistantSkillVersionStatsDto(
    int Version,
    int Runs,
    int Completed,
    int Failed,
    int Proposals,
    int ToolCalls,
    long Tokens);

/// <param name="LiveApproved">Proposals of the live version that people approved or that executed.</param>
/// <param name="LiveRejected">Proposals of the live version that people rejected.</param>
public sealed record NhAssistantSkillComparisonToolDto(
    string ToolId,
    int LiveProposals,
    int CandidateProposals,
    int LiveApproved,
    int LiveRejected);

/// <param name="Status"><c>live</c>, <c>candidate</c>, <c>superseded</c> or <c>discarded</c>.</param>
public sealed record NhAssistantDataSkillVersionDto(
    int Version,
    string Status,
    string Description,
    string? InstructionsHash,
    DateTimeOffset CreatedAt,
    string CreatedBy,
    string? Reason)
{
    /// <summary>
    /// The asset of the application the version takes its instructions from; <see langword="null"/>
    /// for inline instructions or none.
    /// </summary>
    public NhAssistantInstructionAssetReference? InstructionsAsset { get; init; }
}

/// <param name="Source"><c>event</c> or <c>schedule</c>.</param>
/// <param name="Conflict"><c>coalesce</c>, <c>queue</c>, <c>supersede</c> or <c>ignore</c>.</param>
public sealed record NhAssistantDataTriggerDto(
    string TriggerId,
    string? SkillId,
    string Source,
    string? Event,
    string? Cron,
    string? TimeZone,
    string? SubjectSet,
    string? Message,
    string Conflict,
    int DebounceSeconds,
    bool Shadow,
    int Version,
    DateTimeOffset ChangedAt,
    string ChangedBy,
    string? Reason,
    bool InEffect,
    string? Problem)
{
    /// <summary>
    /// For an event trigger: the names of the event's filters it applies, in their order; every one
    /// must accept an event. A name the event no longer registers keeps the trigger from firing and
    /// gives it the problem <c>unknown-filter</c>.
    /// </summary>
    public IReadOnlyList<string> Filters { get; init; } = [];
}

/// <param name="Description">What the skill is for, at most 300 characters; triage and
/// <c>use_skill</c> choose by it.</param>
/// <param name="Instructions">Read after the agent's own, at most 20,000 characters.</param>
/// <param name="Tools">Tool ids or <c>prefix.*</c> selectors within the agent's own; empty uses the
/// agent's.</param>
/// <param name="Profile">A model profile with chat, function calling and streaming.</param>
/// <param name="RuleHandler">The name of a rule handler registered with <c>AddRuleHandler</c>.</param>
/// <param name="DailyToolCalls">With <paramref name="DailyTokens"/>, a daily budget of the skill.</param>
/// <param name="ExpectedVersion">The live version the change was made on; <see langword="null"/>
/// for a new skill.</param>
/// <param name="Reason">At most 500 characters.</param>
/// <param name="Apply">For a change of a skill: <c>live</c> (default) puts the version into effect at
/// once; <c>shadow</c> makes it the candidate that runs in shadow next to the live version.</param>
/// <param name="StartsOff">For a new skill only: it starts off, so triggers that name it are ignored
/// and rules and triage do not route to it until someone switches it on. A change of a skill fails
/// with a field error <c>startsOff</c>.</param>
/// <param name="DataClassification">The classification of the data the skill's model calls carry:
/// <c>public</c>, <c>internal</c>, <c>confidential</c> or <c>restricted</c>; <see langword="null"/> uses
/// the agent's. The model profile of its runs or one of the profile's fallbacks must permit it, or its
/// model calls are refused; the save succeeds either way and the skill then reports the warning
/// <c>classification-not-permitted</c>.</param>
/// <param name="AcceptsFiles">A run in the skill reads files, when its agent accepts them too;
/// <see langword="false"/> (default) refuses them.</param>
/// <param name="MaxFileBytes">With <paramref name="AcceptsFiles"/>: the largest file in bytes, from 1 to
/// the application's <see cref="NhAssistantLimits.MaxFileBytes"/>; <see langword="null"/> keeps the
/// agent's. A turn in the skill takes the lowest of the application's, the agent's and the skill's. The
/// other file limits follow the same rule.</param>
/// <param name="MaxFilePages">The most pages of one file, at most the application's.</param>
/// <param name="MaxFilesPerTurn">The most files of one turn, at most the application's.</param>
/// <param name="MaxFileTokensPerTurn">The most estimated file tokens of one turn, at most the
/// application's.</param>
public sealed record NhAssistantDataSkillRequest(
    string? Description,
    string? Instructions = null,
    IReadOnlyList<string>? Tools = null,
    string? Profile = null,
    string? RuleHandler = null,
    long? MaxTokensPerRun = null,
    int? DailyToolCalls = null,
    long? DailyTokens = null,
    string? ApprovalPolicy = null,
    int? ExpectedVersion = null,
    string? Reason = null,
    string? Apply = null,
    bool StartsOff = false,
    string? DataClassification = null,
    bool AcceptsFiles = false,
    int? MaxFileBytes = null,
    int? MaxFilePages = null,
    int? MaxFilesPerTurn = null,
    int? MaxFileTokensPerTurn = null)
{
    /// <summary>
    /// The asset of the application the version takes its instructions from, instead of
    /// <see cref="Instructions"/> (<c>instructions: not-allowed</c> next to it): its id, exact version
    /// and the SHA-256 of its content. The save asks the application's text asset source:
    /// <c>instructionsAsset: not-allowed</c> without a source (<c>UseTextAssetSource</c>),
    /// <c>instructionsAsset: invalid</c> for a malformed reference or an asset that cannot be
    /// instructions, <c>instructionsAsset: not-found</c> for an asset or version the source does not
    /// have, <c>instructionsAsset.hash: invalid</c> for another hash, and
    /// <c>503 assistant-instructions-asset-unavailable</c> when the source fails. A new version of the
    /// asset is the skill's next version, saved like any change.
    /// </summary>
    public NhAssistantInstructionAssetReference? InstructionsAsset { get; init; }
}

/// <summary>
/// How a change of a data skill takes effect.
/// </summary>
public static class NhAssistantSkillApplyModes
{
    /// <summary>The version takes effect at once.</summary>
    public const string Live = "live";

    /// <summary>The version becomes the candidate that runs in shadow next to the live version.</summary>
    public const string Shadow = "shadow";
}

/// <param name="Skill">The skill the runs work in; <see langword="null"/> lets the agent's rules or
/// triage choose.</param>
/// <param name="Source"><c>event</c> or <c>schedule</c>.</param>
/// <param name="Event">For an event trigger: a trigger event registered with
/// <c>AddTriggerEvent</c>.</param>
/// <param name="Cron">For a schedule: a five- or six-field cron expression.</param>
/// <param name="TimeZone">For a schedule: an IANA or Windows time zone id; UTC by default.</param>
/// <param name="SubjectSet">For a schedule: a subject set registered with <c>AddSubjectSet</c>. Without
/// one the schedule runs for the whole application, once per tick, or once per listed division for an
/// agent that works per division; only an agent that works on the application may have such a
/// schedule.</param>
/// <param name="Message">For a schedule: what every run receives, at most 2,000 characters.</param>
/// <param name="Conflict">What happens while a run on the subject is active: <c>coalesce</c>,
/// <c>queue</c>, <c>supersede</c> or <c>ignore</c>. Events coalesce and schedules ignore by
/// default.</param>
/// <param name="DebounceSeconds">For an event trigger: the window in which events coalesce, at most
/// 3,600; 120 by default.</param>
/// <param name="ExpectedVersion">The trigger's version the change was made on;
/// <see langword="null"/> for a new trigger.</param>
/// <param name="Reason">At most 500 characters.</param>
/// <param name="Filters">For an event trigger: names of the event's filters, as the run catalog lists
/// them, that must all accept an event, at most 10; empty or <see langword="null"/> applies none. A
/// name given twice counts once. A name the event does not register fails with
/// <c>assistant-trigger-filter-not-found</c>; a schedule takes none (<c>filters: invalid</c>).
/// Changing them is a change of the trigger: its next version, audited.</param>
public sealed record NhAssistantDataTriggerRequest(
    string? Source,
    string? Skill = null,
    string? Event = null,
    string? Cron = null,
    string? TimeZone = null,
    string? SubjectSet = null,
    string? Message = null,
    string? Conflict = null,
    int? DebounceSeconds = null,
    int? ExpectedVersion = null,
    string? Reason = null,
    IReadOnlyList<string>? Filters = null);

internal sealed class NhAssistantAgentConfiguration(
    NhAssistantRegistrationState registration,
    NhAssistantRunConfigurations configurations,
    NhAssistantRunConfigurationStore store,
    NhAssistantRunConfigurationValidator validator,
    NhAssistantAgentCatalog agents,
    NhAssistantAgentEvents events,
    NhAssistantRunStore runs,
    IServiceProvider services) : INhAssistantAgentConfiguration
{
    private const int MaxActorIdLength = 256;
    private const int DefaultDebounceSeconds = 120;

    public NhAssistantRunCatalogDto GetCatalog()
    {
        var profiles = services.GetService(typeof(INhAiModelProfileRegistry)) is INhAiModelProfileRegistry registry
            ? registry.Profiles
                .Where(profile => (profile.Capabilities & (NhAiModelCapability.Chat | NhAiModelCapability.FunctionCalling | NhAiModelCapability.Streaming))
                                  == (NhAiModelCapability.Chat | NhAiModelCapability.FunctionCalling | NhAiModelCapability.Streaming))
                .Select(profile => profile.Name)
                .Order(StringComparer.Ordinal)
                .ToArray()
            : [];
        var policies = registration.ApprovalPolicies.Values.OrderBy(policy => policy.Id, StringComparer.Ordinal).ToArray();
        return new NhAssistantRunCatalogDto(
            registration.TriggerEvents.Values
                .OrderBy(item => item.Name, StringComparer.Ordinal)
                .Select(item => new NhAssistantRunBlockDto(item.Name, item.Description)
                {
                    SubjectType = item.SubjectType,
                    Filters = item.Filters.Select(filter => new NhAssistantRunBlockDto(filter.Name, filter.Description)).ToArray()
                })
                .ToArray(),
            registration.SubjectSets.Values.OrderBy(item => item.Name, StringComparer.Ordinal).Select(item => new NhAssistantRunBlockDto(item.Name, item.Description)).ToArray(),
            registration.RuleHandlers.Values.OrderBy(item => item.Name, StringComparer.Ordinal).Select(item => new NhAssistantRunBlockDto(item.Name, item.Description)).ToArray(),
            policies.Select(policy => policy.Id).ToArray(),
            profiles)
        {
            ApprovalPolicySummaries = policies.Select(Summarize).ToArray(),
            AccountSearch = services.GetService<IServiceProviderIsService>()?.IsService(typeof(INhActorSearch)) == true,
            SubjectTypes = services.GetRequiredService<NhAssistantSubjectAccessService>().Types
                .Select(type => new NhAssistantSubjectTypeDto(
                    type.Id,
                    type.NameIn(System.Globalization.CultureInfo.CurrentUICulture),
                    type.PluralNameIn(System.Globalization.CultureInfo.CurrentUICulture),
                    type.EntityType,
                    type.EntityTypeFull,
                    type.LinkTemplate,
                    type.Searchable,
                    type.IsSystem))
                .ToArray(),
            AgentPolicies = registration.AgentPolicies.Values
                .OrderBy(item => item.Name, StringComparer.Ordinal)
                .Select(item => new NhAssistantRunBlockDto(item.Name, item.Description))
                .ToArray(),
            Divisions = services.GetRequiredService<NhAssistantWorksOnRule>().DivisionsListed,
            AgentGroups = services.GetRequiredService<INhAssistantAgentGroups>().ListGroups(),
            FileLimits = new NhAssistantFileLimitsDto(
                registration.Limits.MaxFileBytes,
                registration.Limits.MaxFilePages,
                registration.Limits.MaxFilesPerTurn,
                registration.Limits.MaxFileTokensPerTurn),
            ConsultationCeiling = NhAssistantConsultationCeilingDto.From(registration.ConsultationsAsData),
            AgentMetadata = services.GetRequiredService<INhAssistantAgentMetadata>().ListEntries(),
            InstructionAssets = services.GetRequiredService<NhAssistantInstructionAssets>().Supported
        };
    }

    public async Task<TaskResult<IReadOnlyList<NhAssistantAgentMetadataDto>>> GetMetadataAsync(
        string agentId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        var states = await services.GetRequiredService<NhAssistantAgentMetadataService>().StatesAsync(agentId, cancellationToken);
        return states is null
            ? TaskResult<IReadOnlyList<NhAssistantAgentMetadataDto>>.Failed(NhAssistantErrorCodes.AgentNotFound, "The agent was not found.")
            : TaskResult<IReadOnlyList<NhAssistantAgentMetadataDto>>.Succeeded((IReadOnlyList<NhAssistantAgentMetadataDto>)states.Select(ToDto).ToArray());
    }

    public async Task<TaskResult<NhAssistantAgentMetadataDto>> SaveMetadataAsync(
        string agentId,
        string name,
        NhAssistantAgentMetadataRequest request,
        string actorId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var saved = await services.GetRequiredService<NhAssistantAgentMetadataService>().SaveAsync(
            agentId,
            name,
            request.Value,
            request.ExpectedVersion,
            actorId,
            cancellationToken);
        return saved.Success
            ? TaskResult<NhAssistantAgentMetadataDto>.Succeeded(ToDto(saved.Data!))
            : TaskResult<NhAssistantAgentMetadataDto>.Failed(saved);
    }

    public async Task<TaskResult<NhAssistantAgentMetadataDto>> RemoveMetadataAsync(
        string agentId,
        string name,
        int? expectedVersion,
        string actorId,
        CancellationToken cancellationToken = default)
    {
        var removed = await services.GetRequiredService<NhAssistantAgentMetadataService>().RemoveAsync(
            agentId,
            name,
            expectedVersion,
            actorId,
            cancellationToken);
        return removed.Success
            ? TaskResult<NhAssistantAgentMetadataDto>.Succeeded(ToDto(removed.Data!))
            : TaskResult<NhAssistantAgentMetadataDto>.Failed(removed);
    }

    private static NhAssistantAgentMetadataDto ToDto(NhAssistantAgentMetadataState state)
    {
        return new NhAssistantAgentMetadataDto(
            state.Entry.Name,
            state.Editable,
            state.Entry.MaxBytes,
            state.Source,
            state.Json is null ? null : NhAssistantAgentMetadataService.Parse(state.Json),
            state.Version,
            state.AgentVersion)
        {
            CodeValue = state.CodeJson is null ? null : NhAssistantAgentMetadataService.Parse(state.CodeJson),
            Indexed = state.Entry.Indexed,
            IndexValue = state.IndexValue,
            ChangedAt = state.ChangedAt,
            ChangedBy = state.ChangedBy
        };
    }

    public async Task<TaskResult<NhAssistantAgentConfigurationDto>> CreateAgentAsync(
        NhAssistantAgentRequest request,
        string actorId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        var requiredPolicy = string.IsNullOrWhiteSpace(request.RequiredPolicy) ? null : request.RequiredPolicy;
        if (requiredPolicy is not null
            && await services.GetRequiredService<IAuthorizationPolicyProvider>().GetPolicyAsync(requiredPolicy) is null)
        {
            return TaskResult<NhAssistantAgentConfigurationDto>.Failed(
                NhAssistantValidationErrors.Failed("requiredPolicy", NhAssistantFieldErrors.NotFound));
        }

        var created = await services.GetRequiredService<NhAssistantAgentAdministration>().CreateAsync(
            new NhAssistantAgentInput(
                request.Id,
                request.DisplayName,
                request.Description,
                request.Instructions,
                request.ToolSelectors,
                [],
                requiredPolicy,
                request.Autonomy,
                request.OfferedInChat,
                NhAssistantAgentWorksOn.FromInput(
                    request.WorksOnSubjectTypes,
                    request.WorksOnAllSubjects,
                    request.WorksOnApplication,
                    request.PerDivision ? true : null,
                    request.RunPolicy,
                    request.ViewPolicy),
                request.StartsOff,
                string.IsNullOrWhiteSpace(request.Profile) ? null : request.Profile.Trim(),
                string.IsNullOrWhiteSpace(request.DataClassification) ? null : request.DataClassification.Trim(),
                request.Metadata,
                request.InstructionsAsset)
            {
                ShadowOf = request.ShadowOf
            },
            actorId,
            cancellationToken);
        if (!created.Success)
        {
            return TaskResult<NhAssistantAgentConfigurationDto>.Failed(created);
        }

        // Another administrator may delete the agent right after it was created.
        var configuration = await GetAsync(request.Id, cancellationToken);
        if (configuration is null)
        {
            return TaskResult<NhAssistantAgentConfigurationDto>.Failed(NhAssistantErrorCodes.AgentNotFound, "The agent was not found.");
        }

        return TaskResult<NhAssistantAgentConfigurationDto>.Succeeded(configuration);
    }

    private static NhAssistantApprovalPolicySummaryDto Summarize(NhAiApprovalPolicy policy)
    {
        return new NhAssistantApprovalPolicySummaryDto(
            policy.Id,
            policy.Approvers.Select(approver => approver switch
            {
                NhAiSubjectRoleApproverRule role => new NhAssistantApproverSummaryDto(
                    "subject-role",
                    role.Role.ToString().ToLowerInvariant(),
                    role.NamedRole),
                NhAiPolicyApproverRule named => new NhAssistantApproverSummaryDto("policy", Policy: named.PolicyName),
                _ => new NhAssistantApproverSummaryDto("initiator")
            }).ToArray(),
            (int)Math.Ceiling(policy.Lifetime.TotalMinutes),
            policy.FourEyes,
            policy.Mode == NhAiApprovalMode.Deferred ? "deferred" : "inline");
    }

    public async Task<NhAssistantAgentConfigurationDto?> GetAsync(string agentId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        var agent = await agents.FindAsync(agentId, includeDisabled: true, cancellationToken);
        var code = registration.CodeAgentRuns.GetValueOrDefault(agentId);
        if (agent is null || (code is null && agent.Source != NhAssistantAgentSources.Admin))
        {
            return null;
        }

        await configurations.EnsureCurrentAsync(cancellationToken);
        var problems = configurations.Problems.Where(problem => problem.AgentId == agentId).ToArray();
        var settings = code is null ? await store.FindRunSettingsAsync(agentId, cancellationToken) : null;
        var versions = await store.ListSkillsAsync(agentId, cancellationToken);
        var skills = versions
            .Where(item => item.Status == NhAssistantSkillDefinitionStatuses.Live)
            .Select(item => ToDto(item, problems) with
            {
                Warnings = WarningsOf(item, agent),
                Candidate = versions
                    .Where(candidate => candidate.SkillId == item.SkillId && candidate.Status == NhAssistantSkillDefinitionStatuses.Candidate)
                    .Select(candidate => ToDto(candidate, problems, NhAssistantRunConfigurationProblems.CandidateKind) with
                    {
                        Warnings = WarningsOf(candidate, agent)
                    })
                    .FirstOrDefault()
            })
            .ToArray();
        var triggers = (await store.ListTriggersAsync(agentId, cancellationToken))
            .Select(item => ToDto(item, problems))
            .ToArray();
        var askers = await AskingAgentsAsync(agentId, cancellationToken);
        var dataConsultations = (await store.ListConsultationsAsync(agentId, cancellationToken))
            .Select(item => ToDto(item, problems, askers))
            .ToArray();
        return new NhAssistantAgentConfigurationDto(
            agentId,
            agent.Definition.ToolSelectors,
            code is null
                ? []
                : code.Skills.Values
                    .Select(skill => new NhAssistantCodeSkillDto(skill.Id, skill.Description) { StartsOff = skill.StartsOff, AcceptsFiles = skill.AcceptsFiles })
                    .ToArray(),
            skills,
            registration.CodeTriggers.Values
                .Where(trigger => trigger.AgentId == agentId)
                .OrderBy(trigger => trigger.Id, StringComparer.Ordinal)
                .Select(trigger => new NhAssistantCodeTriggerDto(trigger.Id, SourceName(trigger.Source), trigger.SkillId)
                {
                    SubjectType = trigger.SubjectType
                })
                .ToArray(),
            triggers,
            problems,
            code is null ? NhAssistantAgentSources.Admin : NhAssistantAgentSources.Code,
            settings is null ? null : ToDto(settings, problems))
        {
            CodeRunSettings = code is null ? null : ToCodeDto(code),
            CodeConsultations = code is null
                ? []
                : code.Consultations
                    .OrderBy(item => item.SubjectType, StringComparer.Ordinal)
                    .Select(item => ToCodeDto(item, askers))
                    .ToArray(),
            DataConsultations = dataConsultations,
            ConsultationCeiling = NhAssistantConsultationCeilingDto.From(registration.ConsultationCeilingOf(agentId))
        };
    }

    private static NhAssistantCodeConsultationDto ToCodeDto(
        NhAssistantConsultationOptions consultation,
        IReadOnlyList<NhAssistantAgent> askers)
    {
        var exposure = new List<string>(3);
        if (consultation.Exposure.HasFlag(NhAiToolExposure.Local))
        {
            exposure.Add("local");
        }
        if (consultation.Exposure.HasFlag(NhAiToolExposure.Agent))
        {
            exposure.Add("agent");
        }
        if (consultation.Exposure.HasFlag(NhAiToolExposure.Mcp))
        {
            exposure.Add("mcp");
        }

        return new NhAssistantCodeConsultationDto(
            consultation.SubjectType,
            consultation.Description,
            consultation.AllowedRoutes,
            consultation.AllowRequests,
            exposure)
        {
            ConsultToolId = consultation.ConsultToolId,
            RequestToolId = consultation.AllowRequests ? consultation.RequestToolId : null,
            AskingAgents = AskersOf(consultation.AgentId, consultation.ConsultToolId, askers),
            ToolSelectors = consultation.ToolSelectors
        };
    }

    /// <summary>
    /// Every agent, with the tool selectors in effect, to tell which of them select a consultation's
    /// tool.
    /// </summary>
    private async Task<IReadOnlyList<NhAssistantAgent>> AskingAgentsAsync(string agentId, CancellationToken cancellationToken)
    {
        return (await agents.ListAsync(includeDisabled: true, cancellationToken))
            .Where(agent => !string.Equals(agent.Id, agentId, StringComparison.Ordinal))
            .ToArray();
    }

    private static IReadOnlyList<string> AskersOf(string agentId, string toolId, IReadOnlyList<NhAssistantAgent> askers)
    {
        return askers
            .Where(agent => !string.Equals(agent.Id, agentId, StringComparison.Ordinal)
                            && agent.Definition.ToolSelectors.Any(selector => NhAssistantRegistrationState.Covers(selector, toolId)))
            .Select(agent => agent.Id)
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    private static NhAssistantCodeRunSettingsDto ToCodeDto(NhAssistantAgentRunOptions code)
    {
        return new NhAssistantCodeRunSettingsDto(
            NhAssistantAccountabilities.From(code.Accountability),
            code.DelegateFromInitiator,
            code.ApprovalPolicyId,
            code.RuleHandlerType is not null)
        {
            StartsOff = code.StartsOff,
            AcceptsFiles = code.AcceptsFiles
        };
    }

    public async Task<TaskResult<NhAssistantAgentRunSettingsDto>> SaveRunSettingsAsync(
        string agentId,
        NhAssistantAgentRunSettingsRequest request,
        string actorId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ArgumentNullException.ThrowIfNull(request);
        var reason = request.Reason;
        if (ValidateChange(actorId, ref reason) is { } invalid)
        {
            return TaskResult<NhAssistantAgentRunSettingsDto>.Failed(invalid);
        }

        var agent = await agents.FindAsync(agentId, includeDisabled: true, cancellationToken);
        if (agent is null)
        {
            return TaskResult<NhAssistantAgentRunSettingsDto>.Failed(NhAssistantErrorCodes.AgentNotFound, "The agent was not found.");
        }
        if (agent.Source != NhAssistantAgentSources.Admin || registration.CodeAgentRuns.ContainsKey(agentId))
        {
            return TaskResult<NhAssistantAgentRunSettingsDto>.Failed(NhAssistantErrorCodes.RunSettingsInCode, "The agent is registered in code.");
        }
        var accountabilityKnown = NhAssistantAccountabilities.TryParse(request.Accountability, out var accountability);
        var serviceAccount = Blank(request.ServiceAccountActorId);
        var elevatedReads = Blank(request.ElevatedReads) ?? NhAssistantElevatedReadNames.Restricted;
        var errors = new NhAssistantValidationErrors()
            .Require(accountabilityKnown, "accountability", request.Accountability is null ? NhAssistantFieldErrors.Required : NhAssistantFieldErrors.Invalid)
            .Require((accountability == NhAiAgentAccountability.ServiceAccount) == (serviceAccount is not null), "serviceAccountActorId", serviceAccount is null ? NhAssistantFieldErrors.Required : NhAssistantFieldErrors.Invalid)
            .Require(serviceAccount is null || serviceAccount.Length <= MaxActorIdLength, "serviceAccountActorId", NhAssistantFieldErrors.TooLong)
            .Require(!request.AllowGatewayReads || accountability != NhAiAgentAccountability.Agent, "allowGatewayReads", NhAssistantFieldErrors.Invalid)
            .Require(NhAssistantElevatedReadNames.TryParse(elevatedReads, out _), "elevatedReads", NhAssistantFieldErrors.Invalid)
            .Require(request.RetryCount is null or >= 0 and <= NhAssistantAgentConfigurationLimits.MaxRetryCount, "retryCount", NhAssistantFieldErrors.Invalid)
            .Require((request.DailyToolCalls is null) == (request.DailyTokens is null) && request.DailyToolCalls is null or >= 1 && request.DailyTokens is null or >= 1, "dailyBudget", NhAssistantFieldErrors.Invalid)
            .Require(request.MaxConcurrentRuns is null or >= 1 and <= NhAssistantAgentConfigurationLimits.MaxConcurrentRuns, "maxConcurrentRuns", NhAssistantFieldErrors.Invalid)
            .Require(request.MaxRunsPerSubjectPerDay is null or >= 1 and <= NhAssistantAgentConfigurationLimits.MaxRunsPerSubjectPerDay, "maxRunsPerSubjectPerDay", NhAssistantFieldErrors.Invalid)
            .Require(request.MaxTokensPerRun is null or >= 1, "maxTokensPerRun", NhAssistantFieldErrors.Invalid);
        RequireFileLimits(errors, request.AcceptsFiles, request.MaxFileBytes, request.MaxFilePages, request.MaxFilesPerTurn, request.MaxFileTokensPerTurn);
        if (request.AllowGatewayReads)
        {
            // Gateway reads need the host to issue a person's delegated read credential, as in code.
            errors.Require(
                services.GetService<INhAiDelegatedReadCredentialIssuer>() is not null && services.GetServices<INhAiDelegatedReadSurface>().Any(),
                "allowGatewayReads",
                NhAssistantFieldErrors.Invalid);
        }
        if (!errors.IsEmpty)
        {
            return TaskResult<NhAssistantAgentRunSettingsDto>.Failed(errors.ToResult());
        }

        var settings = new AssistantAgentRunConfiguration
        {
            AgentId = agentId,
            Accountability = NhAssistantAccountabilities.From(accountability),
            ServiceAccountActorId = serviceAccount,
            DelegateFromInitiator = request.DelegateFromInitiator,
            AllowGatewayReads = request.AllowGatewayReads,
            ElevatedReads = elevatedReads,
            ApprovalPolicyId = Blank(request.ApprovalPolicy),
            RetryCount = request.RetryCount ?? 3,
            DailyToolCalls = request.DailyToolCalls,
            DailyTokens = request.DailyTokens,
            MaxConcurrentRuns = request.MaxConcurrentRuns ?? 4,
            MaxRunsPerSubjectPerDay = request.MaxRunsPerSubjectPerDay ?? 10,
            MaxTokensPerRun = request.MaxTokensPerRun,
            TriageProfileName = Blank(request.TriageProfile),
            RuleHandlerName = Blank(request.RuleHandler),
            AcceptsFiles = request.AcceptsFiles,
            MaxFileBytes = request.MaxFileBytes,
            MaxFilePages = request.MaxFilePages,
            MaxFilesPerTurn = request.MaxFilesPerTurn,
            MaxFileTokensPerTurn = request.MaxFileTokensPerTurn,
            UpdatedAt = DateTimeOffset.UtcNow,
            UpdatedBy = actorId,
            Reason = reason
        };
        if (validator.ValidateRunSettings(settings) is { } problem)
        {
            return TaskResult<NhAssistantAgentRunSettingsDto>.Failed(problem switch
            {
                NhAssistantRunConfigurationProblems.UnknownPolicy => NhAssistantValidationErrors.Failed("approvalPolicy", NhAssistantFieldErrors.NotFound),
                NhAssistantRunConfigurationProblems.UnknownProfile => NhAssistantValidationErrors.Failed("triageProfile", NhAssistantFieldErrors.NotFound),
                NhAssistantRunConfigurationProblems.UnknownRuleHandler => NhAssistantValidationErrors.Failed("ruleHandler", NhAssistantFieldErrors.NotFound),
                _ => NhAssistantValidationErrors.Failed("accountability", NhAssistantFieldErrors.Invalid)
            });
        }
        if (serviceAccount is not null && !await AccountExistsAsync(agentId, serviceAccount, cancellationToken))
        {
            return TaskResult<NhAssistantAgentRunSettingsDto>.Failed(
                NhAssistantErrorCodes.ServiceAccountNotFound,
                "No account that may sign in exists with that id.");
        }

        var saved = await store.SaveRunSettingsAsync(settings, request.ExpectedVersion, cancellationToken);
        if (!saved.Succeeded)
        {
            return TaskResult<NhAssistantAgentRunSettingsDto>.Failed(NhAssistantAdminErrorCodes.VersionConflict, "The run settings changed in the meantime.");
        }
        if (serviceAccount is not null)
        {
            // The agent's threads answer to the account, unless an administrator replaced it at runtime.
            await runs.SetServiceAccountOfThreadsAsync(agentId, agent.ServiceAccountOverride ?? serviceAccount, cancellationToken);
        }

        await events.RunSettingsChangedAsync(agentId, saved.Value!.Version, saved.Value.Version == 1 ? "created" : "changed", actorId, cancellationToken);
        await configurations.ReloadAsync(cancellationToken);
        return TaskResult<NhAssistantAgentRunSettingsDto>.Succeeded(ToDto(saved.Value, configurations.Problems));
    }

    public async Task<TaskResult> RemoveRunSettingsAsync(
        string agentId,
        int? expectedVersion,
        string actorId,
        string? reason = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        if (ValidateChange(actorId, ref reason) is { } invalid)
        {
            return invalid;
        }
        if (registration.CodeAgentRuns.ContainsKey(agentId))
        {
            return TaskResult.Failed(NhAssistantErrorCodes.RunSettingsInCode, "The agent is registered in code.");
        }

        var removed = await store.RemoveRunSettingsAsync(agentId, expectedVersion, cancellationToken);
        switch (removed.Outcome)
        {
            case NhAssistantStoreOutcome.NotFound:
                return TaskResult.Failed(NhAssistantErrorCodes.AgentNotConfigured, "The agent has no run settings.");
            case NhAssistantStoreOutcome.Conflict:
                return TaskResult.Failed(NhAssistantAdminErrorCodes.VersionConflict, "The run settings changed in the meantime.");
            case NhAssistantStoreOutcome.Rejected:
                return TaskResult.Failed(NhAssistantErrorCodes.RunSettingsInUse, "Data skills, triggers or consultations still belong to the agent.");
        }

        await events.RunSettingsChangedAsync(agentId, removed.Value!.Version, "removed", actorId, cancellationToken);
        await configurations.ReloadAsync(cancellationToken);
        return TaskResult.Succeeded();
    }

    public async Task<IReadOnlyList<NhAssistantDataSkillVersionDto>> GetSkillVersionsAsync(
        string agentId,
        string skillId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(skillId);
        return (await store.ListSkillsAsync(agentId, cancellationToken))
            .Where(item => item.SkillId == skillId)
            .Select(item => new NhAssistantDataSkillVersionDto(item.Version, item.Status, item.Description, item.InstructionsHash, item.CreatedAt, item.CreatedBy, item.Reason)
            {
                InstructionsAsset = NhAssistantInstructionAssets.ReferenceOf(item)
            })
            .ToArray();
    }

    public async Task<TaskResult<NhAssistantDataSkillDto>> SaveSkillAsync(
        string agentId,
        string skillId,
        NhAssistantDataSkillRequest request,
        string actorId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(skillId);
        ArgumentNullException.ThrowIfNull(request);
        var reason = request.Reason;
        if (ValidateChange(actorId, ref reason) is { } invalid)
        {
            return TaskResult<NhAssistantDataSkillDto>.Failed(invalid);
        }
        var (refused, runAgent) = await ResolveAgentAsync(agentId, cancellationToken);
        if (refused is not null)
        {
            return TaskResult<NhAssistantDataSkillDto>.Failed(refused);
        }
        if (runAgent!.CodeSkillIds.Contains(skillId, StringComparer.Ordinal))
        {
            return TaskResult<NhAssistantDataSkillDto>.Failed(NhAssistantErrorCodes.SkillInCode, "The skill is declared in code.");
        }

        var description = request.Description?.Trim();
        var instructions = string.IsNullOrWhiteSpace(request.Instructions) ? null : request.Instructions.Trim();
        var instructionsAsset = request.InstructionsAsset;
        var tools = (request.Tools ?? []).Select(tool => tool?.Trim() ?? string.Empty).Distinct(StringComparer.Ordinal).ToArray();
        var errors = new NhAssistantValidationErrors()
            .Require(NhAiSubjectRef.IsValidType(skillId), "skillId", NhAssistantFieldErrors.Invalid)
            .Require(!string.IsNullOrEmpty(description), "description", NhAssistantFieldErrors.Required)
            .Require(description is null || description.Length <= NhAssistantSkillBuilder.MaxDescriptionLength, "description", NhAssistantFieldErrors.TooLong)
            .Require(instructions is null || instructions.Length <= NhAssistantAgentConfigurationLimits.MaxSkillInstructionsLength, "instructions", NhAssistantFieldErrors.TooLong)
            // A version takes its instructions from an asset of the application or has text of its own.
            .Require(instructionsAsset is null || instructions is null, "instructions", NhAssistantFieldErrors.NotAllowed)
            .Require(request.MaxTokensPerRun is null or >= 1, "maxTokensPerRun", NhAssistantFieldErrors.Invalid)
            .Require((request.DailyToolCalls is null) == (request.DailyTokens is null) && request.DailyToolCalls is null or >= 1 && request.DailyTokens is null or >= 1, "dailyBudget", NhAssistantFieldErrors.Invalid)
            .Require(request.Apply is null or NhAssistantSkillApplyModes.Live or NhAssistantSkillApplyModes.Shadow, "apply", NhAssistantFieldErrors.Invalid)
            // A new skill has no live version to evaluate a candidate next to; it starts in shadow.
            .Require(request.Apply != NhAssistantSkillApplyModes.Shadow || request.ExpectedVersion is not null, "apply", NhAssistantFieldErrors.Invalid)
            // Only a new skill starts off; a skill that exists keeps its state.
            .Require(!request.StartsOff || request.ExpectedVersion is null, "startsOff", NhAssistantFieldErrors.Invalid)
            .Require(
                Blank(request.DataClassification) is not { } classificationName
                    || NhAssistantDataClassificationNames.TryParse(classificationName, out _),
                "dataClassification",
                NhAssistantFieldErrors.Invalid);
        RequireFileLimits(errors, request.AcceptsFiles, request.MaxFileBytes, request.MaxFilePages, request.MaxFilesPerTurn, request.MaxFileTokensPerTurn);
        // The save asks the application's source: the asset must exist and have the hash it names.
        if (instructionsAsset is not null
            && await services.GetRequiredService<NhAssistantInstructionAssets>().ValidateForSaveAsync(
                instructionsAsset,
                NhAssistantAgentConfigurationLimits.MaxSkillInstructionsLength,
                errors,
                cancellationToken) is { } unavailable)
        {
            return TaskResult<NhAssistantDataSkillDto>.Failed(unavailable);
        }
        if (!errors.IsEmpty)
        {
            return TaskResult<NhAssistantDataSkillDto>.Failed(errors.ToResult());
        }

        var asCandidate = request.Apply == NhAssistantSkillApplyModes.Shadow;
        var definition = new AssistantAgentSkillDefinition
        {
            AgentId = agentId,
            SkillId = skillId,
            Description = description!,
            Instructions = instructions,
            InstructionsHash = instructionsAsset?.Hash ?? (instructions is null ? null : NhAssistantAgentCatalog.ComputeHash(instructions)),
            InstructionsAssetId = instructionsAsset?.AssetId,
            InstructionsAssetVersion = instructionsAsset?.Version,
            ToolSelectorsJson = JsonSerializer.Serialize(tools),
            ProfileName = Blank(request.Profile),
            DataClassification = Blank(request.DataClassification),
            AcceptsFiles = request.AcceptsFiles,
            MaxFileBytes = request.MaxFileBytes,
            MaxFilePages = request.MaxFilePages,
            MaxFilesPerTurn = request.MaxFilesPerTurn,
            MaxFileTokensPerTurn = request.MaxFileTokensPerTurn,
            RuleHandlerName = Blank(request.RuleHandler),
            MaxTokensPerRun = request.MaxTokensPerRun,
            DailyToolCalls = request.DailyToolCalls,
            DailyTokens = request.DailyTokens,
            ApprovalPolicyId = Blank(request.ApprovalPolicy),
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedBy = actorId,
            Reason = reason
        };
        if (validator.ValidateSkill(definition, tools, runAgent) is { } problem)
        {
            return TaskResult<NhAssistantDataSkillDto>.Failed(SkillFieldError(problem));
        }

        var live = await LiveSkillsAsync(agentId, cancellationToken);
        if (request.ExpectedVersion is null
            && !live.ContainsKey(skillId)
            && runAgent.CodeSkillIds.Count + live.Count >= NhAssistantAgentRunOptions.MaxSkills)
        {
            return TaskResult<NhAssistantDataSkillDto>.Failed(NhAssistantErrorCodes.ConfigurationLimit, "The agent has as many skills as allowed.");
        }

        var saved = await store.AddSkillVersionAsync(definition, request.ExpectedVersion, cancellationToken, asCandidate);
        if (!saved.Succeeded)
        {
            return TaskResult<NhAssistantDataSkillDto>.Failed(NhAssistantAdminErrorCodes.VersionConflict, "The skill changed in the meantime.");
        }
        if (request.StartsOff)
        {
            // Off before the configuration reloads, so no trigger reaches the new skill first.
            await services.GetRequiredService<NhAssistantAdminStore>()
                .SetSkillStateAsync(agentId, skillId, NhAssistantAgentRunStates.Off, actorId, reason, cancellationToken);
            await events.AgentStateChangedAsync(agentId, actorId, NhAssistantAgentRunStates.Off, 0, cancellationToken, skillId);
        }

        await events.SkillDefinitionChangedAsync(
            agentId,
            skillId,
            saved.Value!.Version,
            saved.Value.InstructionsHash,
            asCandidate ? "candidate" : saved.Value.Version == 1 || request.ExpectedVersion is null ? "created" : "changed",
            actorId,
            cancellationToken,
            NhAssistantInstructionAssets.ReferenceOf(saved.Value)?.InstructionsVersion);
        await configurations.ReloadAsync(cancellationToken);
        // The save succeeds also when no profile permits the skill's classification; the warning
        // tells the administrator that its model calls will be refused.
        var dto = asCandidate
            ? ToDto(saved.Value, configurations.Problems, NhAssistantRunConfigurationProblems.CandidateKind)
            : ToDto(saved.Value, configurations.Problems);
        return TaskResult<NhAssistantDataSkillDto>.Succeeded(dto with
        {
            Warnings = await WarningsOfAsync(saved.Value, cancellationToken)
        });
    }

    public async Task<TaskResult<NhAssistantDataSkillDto>> PromoteSkillCandidateAsync(
        string agentId,
        string skillId,
        int candidateVersion,
        string actorId,
        string? reason = null,
        CancellationToken cancellationToken = default)
    {
        var changed = await ChangeCandidateAsync(agentId, skillId, candidateVersion, actorId, reason, promote: true, cancellationToken);
        if (changed.Failure is { } failure)
        {
            return TaskResult<NhAssistantDataSkillDto>.Failed(failure);
        }

        return TaskResult<NhAssistantDataSkillDto>.Succeeded(ToDto(changed.Definition!, configurations.Problems) with
        {
            Warnings = await WarningsOfAsync(changed.Definition!, cancellationToken)
        });
    }

    public async Task<TaskResult> DiscardSkillCandidateAsync(
        string agentId,
        string skillId,
        int candidateVersion,
        string actorId,
        string? reason = null,
        CancellationToken cancellationToken = default)
    {
        var changed = await ChangeCandidateAsync(agentId, skillId, candidateVersion, actorId, reason, promote: false, cancellationToken);
        return changed.Failure ?? TaskResult.Succeeded();
    }

    public async Task<TaskResult<NhAssistantSkillComparisonDto>> CompareSkillCandidateAsync(
        string agentId,
        string skillId,
        DateTimeOffset? from = null,
        DateTimeOffset? to = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(skillId);
        var versions = (await store.ListSkillsAsync(agentId, cancellationToken)).Where(item => item.SkillId == skillId).ToArray();
        var candidate = versions.FirstOrDefault(item => item.Status == NhAssistantSkillDefinitionStatuses.Candidate);
        var live = versions.FirstOrDefault(item => item.Status == NhAssistantSkillDefinitionStatuses.Live);
        if (candidate is null || live is null)
        {
            return TaskResult<NhAssistantSkillComparisonDto>.Failed(NhAssistantErrorCodes.SkillCandidateNotFound, "The skill has no candidate.");
        }

        var until = to ?? DateTimeOffset.UtcNow;
        var since = from ?? candidate.CreatedAt;
        var skillRuns = await runs.GetSkillVersionRunsAsync(agentId, skillId, [live.Version, candidate.Version], since, until, cancellationToken);
        var liveRuns = skillRuns.Where(run => run.SkillVersion == live.Version && !run.SkillCandidate).ToArray();
        var candidateRuns = skillRuns.Where(run => run.SkillVersion == candidate.Version && run.SkillCandidate).ToArray();
        var proposals = await runs.GetProposalsOfRunsAsync(liveRuns.Concat(candidateRuns).Select(run => run.Id).ToArray(), cancellationToken);
        var liveIds = liveRuns.Select(run => run.Id).ToHashSet();
        var candidateIds = candidateRuns.Select(run => run.Id).ToHashSet();

        // A candidate's run and the live run it ran next to share the trigger and the event's key.
        var liveEvents = liveRuns
            .Where(run => run.TriggerEventKey is not null)
            .Select(run => (run.TriggerId, Key: NhAssistantAgentTriggers.CandidateKey(run.TriggerEventKey, candidate.Version)))
            .ToHashSet();
        var paired = candidateRuns.Count(run => run.TriggerEventKey is not null && liveEvents.Contains((run.TriggerId, run.TriggerEventKey)));
        var tools = proposals
            .GroupBy(item => item.ToolId, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => new NhAssistantSkillComparisonToolDto(
                group.Key,
                group.Count(item => liveIds.Contains(item.RunId)),
                group.Count(item => candidateIds.Contains(item.RunId)),
                group.Count(item => liveIds.Contains(item.RunId)
                    && item.Status is NhAssistantApprovalStatuses.Approved
                        or NhAssistantApprovalStatuses.AwaitingApplication
                        or NhAssistantApprovalStatuses.Executed),
                group.Count(item => liveIds.Contains(item.RunId) && item.Status == NhAssistantApprovalStatuses.Rejected)))
            .ToArray();
        return TaskResult<NhAssistantSkillComparisonDto>.Succeeded(new NhAssistantSkillComparisonDto(
            agentId,
            skillId,
            since,
            until,
            Stats(live.Version, liveRuns, proposals, liveIds),
            Stats(candidate.Version, candidateRuns, proposals, candidateIds),
            paired,
            tools));
    }

    private static NhAssistantSkillVersionStatsDto Stats(
        int version,
        IReadOnlyCollection<AssistantRun> versionRuns,
        IReadOnlyList<(Guid RunId, string ToolId, string Status)> proposals,
        IReadOnlySet<Guid> runIds)
    {
        return new NhAssistantSkillVersionStatsDto(
            version,
            versionRuns.Count,
            versionRuns.Count(run => run.Status == NhAssistantRunStatuses.Completed),
            versionRuns.Count(run => run.Status == NhAssistantRunStatuses.Failed),
            proposals.Count(item => runIds.Contains(item.RunId)),
            versionRuns.Sum(run => run.ToolCalls),
            versionRuns.Sum(run => run.InputTokens + run.OutputTokens));
    }

    private async Task<(TaskResult? Failure, AssistantAgentSkillDefinition? Definition)> ChangeCandidateAsync(
        string agentId,
        string skillId,
        int candidateVersion,
        string actorId,
        string? reason,
        bool promote,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(skillId);
        if (ValidateChange(actorId, ref reason) is { } invalid)
        {
            return (invalid, null);
        }

        var changed = promote
            ? await store.PromoteCandidateAsync(agentId, skillId, candidateVersion, cancellationToken)
            : await store.DiscardCandidateAsync(agentId, skillId, candidateVersion, cancellationToken);
        switch (changed.Outcome)
        {
            case NhAssistantStoreOutcome.NotFound:
                return (TaskResult.Failed(NhAssistantErrorCodes.SkillCandidateNotFound, "The skill has no candidate."), null);
            case NhAssistantStoreOutcome.Conflict:
                return (TaskResult.Failed(NhAssistantAdminErrorCodes.VersionConflict, "The candidate changed in the meantime."), null);
        }

        await events.SkillDefinitionChangedAsync(
            agentId,
            skillId,
            changed.Value!.Version,
            changed.Value.InstructionsHash,
            promote ? "promoted" : "discarded",
            actorId,
            cancellationToken,
            NhAssistantInstructionAssets.ReferenceOf(changed.Value)?.InstructionsVersion);
        await configurations.ReloadAsync(cancellationToken);
        return (null, changed.Value);
    }

    public async Task<TaskResult> RemoveSkillAsync(
        string agentId,
        string skillId,
        int? expectedVersion,
        string actorId,
        string? reason = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(skillId);
        if (ValidateChange(actorId, ref reason) is { } invalid)
        {
            return invalid;
        }
        var (refused, runAgent) = await ResolveAgentAsync(agentId, cancellationToken);
        if (refused is not null)
        {
            return refused;
        }
        if (runAgent!.CodeSkillIds.Contains(skillId, StringComparer.Ordinal))
        {
            return TaskResult.Failed(NhAssistantErrorCodes.SkillInCode, "The skill is declared in code.");
        }

        var removed = await store.RemoveSkillAsync(agentId, skillId, expectedVersion, cancellationToken);
        switch (removed.Outcome)
        {
            case NhAssistantStoreOutcome.NotFound:
                return TaskResult.Failed(NhAssistantErrorCodes.SkillNotFound, "The agent has no such data skill.");
            case NhAssistantStoreOutcome.Conflict:
                return TaskResult.Failed(NhAssistantAdminErrorCodes.VersionConflict, "The skill changed in the meantime.");
            case NhAssistantStoreOutcome.Rejected:
                return TaskResult.Failed(NhAssistantErrorCodes.SkillInUse, "A data trigger names the skill.");
        }

        await events.SkillDefinitionChangedAsync(
            agentId,
            skillId,
            removed.Value!.Version,
            removed.Value.InstructionsHash,
            "removed",
            actorId,
            cancellationToken,
            NhAssistantInstructionAssets.ReferenceOf(removed.Value)?.InstructionsVersion);
        await configurations.ReloadAsync(cancellationToken);
        return TaskResult.Succeeded();
    }

    public async Task<TaskResult<NhAssistantDataTriggerDto>> SaveTriggerAsync(
        string agentId,
        string triggerId,
        NhAssistantDataTriggerRequest request,
        string actorId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(triggerId);
        ArgumentNullException.ThrowIfNull(request);
        var reason = request.Reason;
        if (ValidateChange(actorId, ref reason) is { } invalid)
        {
            return TaskResult<NhAssistantDataTriggerDto>.Failed(invalid);
        }
        var (refused, _) = await ResolveAgentAsync(agentId, cancellationToken);
        if (refused is not null)
        {
            return TaskResult<NhAssistantDataTriggerDto>.Failed(refused);
        }
        if (registration.CodeTriggers.ContainsKey(triggerId))
        {
            return TaskResult<NhAssistantDataTriggerDto>.Failed(NhAssistantErrorCodes.TriggerInCode, "The trigger is registered in code.");
        }

        var schedule = request.Source == NhAssistantDataTriggerSources.Schedule;
        var conflict = Blank(request.Conflict) ?? (schedule ? NhAssistantTriggerConflictNames.Ignore : NhAssistantTriggerConflictNames.Coalesce);
        // The filters in the order chosen, each once.
        var filters = (request.Filters ?? [])
            .Select(name => name?.Trim() ?? string.Empty)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var errors = new NhAssistantValidationErrors()
            .Require(NhAiSubjectRef.IsValidType(triggerId) && !NhAssistantTriggerIds.IsReserved(triggerId), "triggerId", NhAssistantFieldErrors.Invalid)
            .Require(request.Source is NhAssistantDataTriggerSources.Event or NhAssistantDataTriggerSources.Schedule, "source", request.Source is null ? NhAssistantFieldErrors.Required : NhAssistantFieldErrors.Invalid)
            .Require(NhAssistantTriggerConflictNames.TryParse(conflict, out _), "conflict", NhAssistantFieldErrors.Invalid)
            .Require(request.DebounceSeconds is null or >= 0 and <= NhAssistantAgentConfigurationLimits.MaxDebounceSeconds, "debounceSeconds", NhAssistantFieldErrors.Invalid)
            .Require(
                filters.Length <= NhAssistantAgentConfigurationLimits.MaxTriggerFilters
                && filters.All(NhAssistantTriggerFilterNames.IsValid)
                && (filters.Length == 0 || !schedule),
                "filters",
                NhAssistantFieldErrors.Invalid);
        if (request.Source == NhAssistantDataTriggerSources.Event)
        {
            errors.Require(!string.IsNullOrWhiteSpace(request.Event), "event", NhAssistantFieldErrors.Required);
        }
        if (schedule)
        {
            // Without a subject set a schedule runs for the whole application; the validator checks
            // that the agent works on it.
            errors
                .Require(!string.IsNullOrWhiteSpace(request.Cron), "cron", NhAssistantFieldErrors.Required)
                .Require(!string.IsNullOrWhiteSpace(request.Message), "message", NhAssistantFieldErrors.Required)
                .Require(request.Message is null || request.Message.Trim().Length <= NhAssistantAgentConfigurationLimits.MaxTriggerMessageLength, "message", NhAssistantFieldErrors.TooLong);
        }
        if (!errors.IsEmpty)
        {
            return TaskResult<NhAssistantDataTriggerDto>.Failed(errors.ToResult());
        }

        var now = DateTimeOffset.UtcNow;
        var row = new AssistantAgentTriggerDefinition
        {
            Id = triggerId,
            AgentId = agentId,
            SkillId = Blank(request.Skill),
            Source = request.Source!,
            EventName = schedule ? null : Blank(request.Event),
            FiltersJson = NhAssistantDataTriggerFilters.Write(filters),
            Cron = schedule ? Blank(request.Cron) : null,
            TimeZone = schedule ? Blank(request.TimeZone) : null,
            SubjectSetName = schedule ? Blank(request.SubjectSet) : null,
            Message = schedule ? request.Message!.Trim() : null,
            Conflict = conflict,
            DebounceSeconds = schedule ? 0 : request.DebounceSeconds ?? DefaultDebounceSeconds,
            UpdatedAt = now,
            UpdatedBy = actorId,
            Reason = reason
        };
        await configurations.EnsureCurrentAsync(cancellationToken);
        if (validator.ValidateTrigger(row, registration.AgentRuns) is { } problem)
        {
            return TaskResult<NhAssistantDataTriggerDto>.Failed(TriggerFieldError(problem, schedule));
        }

        var existing = await store.ListTriggersAsync(agentId, cancellationToken);
        if (request.ExpectedVersion is null
            && existing.All(item => item.Id != triggerId)
            && existing.Count >= NhAssistantAgentConfigurationLimits.MaxTriggersPerAgent)
        {
            return TaskResult<NhAssistantDataTriggerDto>.Failed(NhAssistantErrorCodes.ConfigurationLimit, "The agent has as many data triggers as allowed.");
        }

        var saved = await store.SaveTriggerAsync(row, request.ExpectedVersion, cancellationToken);
        if (saved.Outcome == NhAssistantStoreOutcome.Rejected)
        {
            // The id belongs to a data trigger of another agent; trigger ids are unique.
            return TaskResult<NhAssistantDataTriggerDto>.Failed(NhAssistantValidationErrors.Failed("triggerId", NhAssistantFieldErrors.Invalid));
        }
        if (!saved.Succeeded)
        {
            return TaskResult<NhAssistantDataTriggerDto>.Failed(NhAssistantAdminErrorCodes.VersionConflict, "The trigger changed in the meantime.");
        }

        await events.TriggerDefinitionChangedAsync(
            agentId,
            triggerId,
            saved.Value!.SkillId,
            saved.Value.Version,
            saved.Value.Version == 1 ? "created" : "changed",
            actorId,
            cancellationToken);
        await configurations.ReloadAsync(cancellationToken);
        return TaskResult<NhAssistantDataTriggerDto>.Succeeded(ToDto(saved.Value, configurations.Problems));
    }

    public async Task<TaskResult> RemoveTriggerAsync(
        string agentId,
        string triggerId,
        int? expectedVersion,
        string actorId,
        string? reason = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(triggerId);
        if (ValidateChange(actorId, ref reason) is { } invalid)
        {
            return invalid;
        }
        if (registration.CodeTriggers.TryGetValue(triggerId, out var codeTrigger) && codeTrigger.AgentId == agentId)
        {
            return TaskResult.Failed(NhAssistantErrorCodes.TriggerInCode, "The trigger is registered in code.");
        }

        var removed = await store.RemoveTriggerAsync(agentId, triggerId, expectedVersion, cancellationToken);
        switch (removed.Outcome)
        {
            case NhAssistantStoreOutcome.NotFound:
                return TaskResult.Failed(NhAssistantErrorCodes.TriggerNotFound, "The agent has no such data trigger.");
            case NhAssistantStoreOutcome.Conflict or NhAssistantStoreOutcome.Rejected:
                return TaskResult.Failed(NhAssistantAdminErrorCodes.VersionConflict, "The trigger changed in the meantime.");
        }

        await events.TriggerDefinitionChangedAsync(agentId, triggerId, removed.Value!.SkillId, removed.Value.Version, "removed", actorId, cancellationToken);
        await configurations.ReloadAsync(cancellationToken);
        return TaskResult.Succeeded();
    }

    public async Task<TaskResult<NhAssistantDataTriggerDto>> SetTriggerShadowAsync(
        string agentId,
        string triggerId,
        bool shadow,
        string actorId,
        string? reason = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(triggerId);
        if (ValidateChange(actorId, ref reason) is { } invalid)
        {
            return TaskResult<NhAssistantDataTriggerDto>.Failed(invalid);
        }
        if (registration.CodeTriggers.TryGetValue(triggerId, out var codeTrigger) && codeTrigger.AgentId == agentId)
        {
            return TaskResult<NhAssistantDataTriggerDto>.Failed(NhAssistantErrorCodes.TriggerInCode, "The trigger is registered in code.");
        }

        var before = await store.FindTriggerAsync(triggerId, cancellationToken);
        var changed = await store.SetTriggerShadowAsync(agentId, triggerId, shadow, actorId, reason, cancellationToken);
        if (changed.Outcome == NhAssistantStoreOutcome.NotFound)
        {
            return TaskResult<NhAssistantDataTriggerDto>.Failed(NhAssistantErrorCodes.TriggerNotFound, "The agent has no such data trigger.");
        }
        if (!changed.Succeeded)
        {
            return TaskResult<NhAssistantDataTriggerDto>.Failed(NhAssistantAdminErrorCodes.VersionConflict, "The trigger changed in the meantime.");
        }

        if (before?.Shadow != shadow)
        {
            await events.TriggerDefinitionChangedAsync(
                agentId,
                triggerId,
                changed.Value!.SkillId,
                changed.Value.Version,
                shadow ? "shadow" : "live",
                actorId,
                cancellationToken);
            await configurations.ReloadAsync(cancellationToken);
        }

        return TaskResult<NhAssistantDataTriggerDto>.Succeeded(ToDto(changed.Value!, configurations.Problems));
    }

    public async Task<TaskResult<NhAssistantDataConsultationDto>> SaveConsultationAsync(
        string agentId,
        string subjectType,
        NhAssistantDataConsultationRequest request,
        string actorId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(subjectType);
        ArgumentNullException.ThrowIfNull(request);
        var reason = request.Reason;
        if (ValidateChange(actorId, ref reason) is { } invalid)
        {
            return TaskResult<NhAssistantDataConsultationDto>.Failed(invalid);
        }

        var (refused, agent, runOptions, ceiling) = await ResolveConsultableAsync(agentId, cancellationToken);
        if (refused is not null)
        {
            return TaskResult<NhAssistantDataConsultationDto>.Failed(refused);
        }
        if (registration.CodeAgentRuns.TryGetValue(agentId, out var code)
            && code.Consultations.Any(item => string.Equals(item.SubjectType, subjectType, StringComparison.Ordinal)))
        {
            return TaskResult<NhAssistantDataConsultationDto>.Failed(NhAssistantErrorCodes.ConsultationInCode, "The agent allows consultation about this subject type in code.");
        }

        var description = Blank(request.Description);
        var instructions = Blank(request.Instructions);
        var routes = (request.Routes ?? []).Select(route => route?.Trim() ?? string.Empty).ToArray();
        // No selection, or an empty one, offers every tool of the agent (CT2).
        var toolSelectors = request.ToolSelectors is { Count: > 0 } requested
            ? requested.Select(selector => selector?.Trim() ?? string.Empty).ToArray()
            : null;
        var minimumRole = Blank(request.MinimumRole) ?? NhAssistantSubjectRoleNames.Reader;
        var proposalRole = Blank(request.ProposalRole) ?? NhAssistantSubjectRoleNames.Participant;
        var dataClassification = Blank(request.DataClassification);
        // A new consultation starts with the defaults of code, capped by the ceiling.
        var maxToolCalls = request.MaxToolCalls ?? Math.Min(NhAssistantConsultationBounds.DefaultToolCalls, ceiling!.MaxToolCalls);
        var timeoutSeconds = request.TimeoutSeconds
                             ?? (int)Math.Min(NhAssistantConsultationBounds.DefaultTimeout.TotalSeconds, ceiling!.MaxTimeout.TotalSeconds);
        var maxAnswerChars = request.MaxAnswerChars ?? Math.Min(NhAssistantConsultationBounds.DefaultAnswerChars, ceiling!.MaxAnswerChars);
        var maxHistoryMessages = request.MaxHistoryMessages ?? Math.Min(NhAssistantConsultationBounds.DefaultHistoryMessages, ceiling!.MaxHistoryMessages);
        var errors = new NhAssistantValidationErrors()
            .Require(NhAiSubjectRef.IsValidType(subjectType), "subjectType", NhAssistantFieldErrors.Invalid)
            .Require(description is not null, "description", NhAssistantFieldErrors.Required)
            .Require(description is null || description.Length <= NhAssistantAgentConfigurationLimits.MaxConsultationDescriptionLength, "description", NhAssistantFieldErrors.TooLong)
            .Require(instructions is null || instructions.Length <= NhAssistantAgentConfigurationLimits.MaxConsultationInstructionsLength, "instructions", NhAssistantFieldErrors.TooLong)
            .Require(NhAssistantSubjectRoleNames.TryParse(minimumRole, out _), "minimumRole", NhAssistantFieldErrors.Invalid)
            .Require(NhAssistantSubjectRoleNames.TryParse(proposalRole, out _), "proposalRole", NhAssistantFieldErrors.Invalid)
            .Require(NhAssistantDataConsultationRoutes.IsValid(routes), "routes", NhAssistantFieldErrors.Invalid)
            .Require(toolSelectors is null || NhAssistantDataConsultationToolSelectors.IsValid(toolSelectors), "toolSelectors", NhAssistantFieldErrors.Invalid)
            .Require(
                maxToolCalls is >= 0 and <= NhAssistantConsultationBounds.MaxToolCalls
                && timeoutSeconds >= NhAssistantConsultationBounds.MinTimeout.TotalSeconds
                && timeoutSeconds <= NhAssistantConsultationBounds.MaxTimeout.TotalSeconds
                && maxAnswerChars is >= NhAssistantConsultationBounds.MinAnswerChars and <= NhAssistantConsultationBounds.MaxAnswerChars
                && maxHistoryMessages is >= 0 and <= NhAssistantConsultationBounds.MaxHistoryMessages,
                "limits",
                NhAssistantFieldErrors.Invalid)
            .Require(dataClassification is null || NhAssistantDataClassificationNames.TryParse(dataClassification, out _), "dataClassification", NhAssistantFieldErrors.Invalid);
        if (!errors.IsEmpty)
        {
            return TaskResult<NhAssistantDataConsultationDto>.Failed(errors.ToResult());
        }

        var now = DateTimeOffset.UtcNow;
        var row = new AssistantAgentConsultationDefinition
        {
            AgentId = agentId,
            SubjectType = subjectType,
            Description = description!,
            Instructions = instructions,
            InstructionsHash = instructions is null ? null : NhAssistantAgentCatalog.ComputeHash(instructions),
            MinimumRole = minimumRole,
            AskerPolicy = Blank(request.AskerPolicy),
            RoutesJson = NhAssistantDataConsultationRoutes.Write(routes),
            ProposalRole = proposalRole,
            ProposalPolicyId = Blank(request.ProposalPolicy),
            ToolSelectorsJson = NhAssistantDataConsultationToolSelectors.Write(toolSelectors),
            MaxToolCalls = maxToolCalls,
            TimeoutSeconds = timeoutSeconds,
            MaxAnswerChars = maxAnswerChars,
            MaxHistoryMessages = maxHistoryMessages,
            AllowRequests = request.AllowRequests,
            ExposeToMcp = request.ExposeToMcp,
            DataClassification = dataClassification,
            UpdatedAt = now,
            UpdatedBy = actorId,
            Reason = reason
        };

        // The same check as every load, refused here instead of degraded (AC11).
        var check = validator.CheckConsultation(row, runOptions, agent!.Definition.RequiredPolicy, agent.Definition.ToolSelectors);
        if (check.Problems.Count > 0)
        {
            return TaskResult<NhAssistantDataConsultationDto>.Failed(ConsultationFieldErrors(check.Problems));
        }
        if (services.GetService<INhAssistantReservedToolNames>() is { } reserved
            && (reserved.IsReserved(check.Options!.ConsultToolId, NhAssistantConsultationTools.ExportNameOf(false, agentId, subjectType))
                || (row.AllowRequests && reserved.IsReserved(check.Options.RequestToolId, NhAssistantConsultationTools.ExportNameOf(true, agentId, subjectType)))))
        {
            return TaskResult<NhAssistantDataConsultationDto>.Failed(NhAssistantValidationErrors.Failed("subjectType", NhAssistantFieldErrors.Invalid));
        }

        var existing = await store.ListConsultationsAsync(agentId, cancellationToken);
        if (request.ExpectedVersion is null
            && existing.All(item => item.SubjectType != subjectType)
            && existing.Count >= NhAssistantAgentConfigurationLimits.MaxConsultationsPerAgent)
        {
            return TaskResult<NhAssistantDataConsultationDto>.Failed(NhAssistantErrorCodes.ConfigurationLimit, "The agent has as many consultations defined as data as allowed.");
        }

        var saved = await store.SaveConsultationAsync(row, request.ExpectedVersion, cancellationToken);
        if (!saved.Succeeded)
        {
            return TaskResult<NhAssistantDataConsultationDto>.Failed(NhAssistantAdminErrorCodes.VersionConflict, "The consultation changed in the meantime.");
        }

        await events.ConsultationDefinitionChangedAsync(
            agentId,
            subjectType,
            saved.Value!.Version,
            saved.Value.InstructionsHash,
            saved.Value.Version == 1 ? "created" : "changed",
            actorId,
            cancellationToken);
        await configurations.ReloadAsync(cancellationToken);
        return TaskResult<NhAssistantDataConsultationDto>.Succeeded(
            ToDto(saved.Value, configurations.Problems, await AskingAgentsAsync(agentId, cancellationToken)));
    }

    public async Task<TaskResult> RemoveConsultationAsync(
        string agentId,
        string subjectType,
        int? expectedVersion,
        string actorId,
        string? reason = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(subjectType);
        if (ValidateChange(actorId, ref reason) is { } invalid)
        {
            return invalid;
        }
        if (registration.CodeAgentRuns.TryGetValue(agentId, out var code)
            && code.Consultations.Any(item => string.Equals(item.SubjectType, subjectType, StringComparison.Ordinal)))
        {
            return TaskResult.Failed(NhAssistantErrorCodes.ConsultationInCode, "The agent allows consultation about this subject type in code.");
        }

        var removed = await store.RemoveConsultationAsync(agentId, subjectType, expectedVersion, cancellationToken);
        switch (removed.Outcome)
        {
            case NhAssistantStoreOutcome.NotFound:
                return TaskResult.Failed(NhAssistantErrorCodes.ConsultationNotFound, "The agent has no such consultation defined as data.");
            case NhAssistantStoreOutcome.Conflict or NhAssistantStoreOutcome.Rejected:
                return TaskResult.Failed(NhAssistantAdminErrorCodes.VersionConflict, "The consultation changed in the meantime.");
        }

        await events.ConsultationDefinitionChangedAsync(
            agentId,
            subjectType,
            removed.Value!.Version,
            removed.Value.InstructionsHash,
            "removed",
            actorId,
            cancellationToken);
        await configurations.ReloadAsync(cancellationToken);
        return TaskResult.Succeeded();
    }

    /// <summary>
    /// The agent a consultation defined as data is saved on, with its options in effect and its
    /// ceiling: an agent in code that is consultable as data, or one created in the administration
    /// with run settings (AC18), while the application lets administrators define consultations.
    /// </summary>
    private async Task<(TaskResult? Refused, NhAssistantAgent? Agent, NhAssistantAgentRunOptions? RunOptions, NhAssistantConsultationCeiling? Ceiling)> ResolveConsultableAsync(
        string agentId,
        CancellationToken cancellationToken)
    {
        var agent = await agents.FindAsync(agentId, includeDisabled: true, cancellationToken);
        if (agent is null)
        {
            return (TaskResult.Failed(NhAssistantErrorCodes.AgentNotFound, "The agent was not found."), null, null, null);
        }

        var ceiling = registration.ConsultationCeilingOf(agentId);
        if (ceiling is null || (agent.Source != NhAssistantAgentSources.Admin && !registration.CodeAgentRuns.ContainsKey(agentId)))
        {
            return (TaskResult.Failed(NhAssistantErrorCodes.ConsultationsNotAllowed, "The agent may not get consultations defined as data."), agent, null, null);
        }

        await configurations.EnsureCurrentAsync(cancellationToken);
        if (!registration.AgentRuns.TryGetValue(agentId, out var runOptions)
            || (agent.Source == NhAssistantAgentSources.Admin && await store.FindRunSettingsAsync(agentId, cancellationToken) is null))
        {
            return (TaskResult.Failed(NhAssistantErrorCodes.AgentNotConfigured, "The agent has no run settings."), agent, null, null);
        }

        return (null, agent, runOptions, ceiling);
    }

    /// <summary>
    /// The field error of each problem a consultation would have.
    /// </summary>
    private static TaskResult ConsultationFieldErrors(IReadOnlyList<string> problems)
    {
        if (problems.Contains(NhAssistantRunConfigurationProblems.NotConsultable, StringComparer.Ordinal))
        {
            return TaskResult.Failed(NhAssistantErrorCodes.ConsultationsNotAllowed, "The agent may not get consultations defined as data.");
        }
        if (problems.Contains(NhAssistantRunConfigurationProblems.ConflictsWithCode, StringComparer.Ordinal))
        {
            return TaskResult.Failed(NhAssistantErrorCodes.ConsultationInCode, "The agent allows consultation about this subject type in code.");
        }
        if (problems.Contains(NhAssistantRunConfigurationProblems.UnknownAgent, StringComparer.Ordinal))
        {
            return TaskResult.Failed(NhAssistantErrorCodes.AgentNotConfigured, "The agent has no run settings.");
        }

        var errors = new NhAssistantValidationErrors();
        foreach (var problem in problems)
        {
            _ = problem switch
            {
                NhAssistantRunConfigurationProblems.UnknownSubjectType => errors.Add("subjectType", NhAssistantFieldErrors.NotFound),
                NhAssistantRunConfigurationProblems.NotWorkedOn => errors.Add("subjectType", NhAssistantFieldErrors.Invalid),
                NhAssistantRunConfigurationProblems.UnknownAgentPolicy => errors.Add("askerPolicy", NhAssistantFieldErrors.NotFound),
                NhAssistantRunConfigurationProblems.RouteNotAllowed => errors.Add("routes", NhAssistantFieldErrors.NotAllowed),
                NhAssistantRunConfigurationProblems.AgentAnswersToPerson or NhAssistantRunConfigurationProblems.AgentHasNoRights
                    => errors.Add("routes", NhAssistantFieldErrors.Invalid),
                NhAssistantRunConfigurationProblems.UnknownPolicy => errors.Add("proposalPolicy", NhAssistantFieldErrors.NotFound),
                NhAssistantRunConfigurationProblems.NoApprover => errors.Add("proposalPolicy", NhAssistantFieldErrors.Invalid),
                NhAssistantRunConfigurationProblems.LimitAboveCeiling => errors.Add("limits", NhAssistantFieldErrors.NotAllowed),
                NhAssistantRunConfigurationProblems.RequestsNotAllowed => errors.Add("allowRequests", NhAssistantFieldErrors.NotAllowed),
                NhAssistantRunConfigurationProblems.McpNotAllowed => errors.Add("exposeToMcp", NhAssistantFieldErrors.NotAllowed),
                NhAssistantRunConfigurationProblems.ToolsNotAllowed => errors.Add("toolSelectors", NhAssistantFieldErrors.NotAllowed),
                _ => errors.Add("subjectType", NhAssistantFieldErrors.Invalid)
            };
        }

        return errors.ToResult();
    }

    private NhAssistantDataConsultationDto ToDto(
        AssistantAgentConsultationDefinition item,
        IReadOnlyList<NhAssistantRunConfigurationProblem> problems,
        IReadOnlyList<NhAssistantAgent> askers)
    {
        var own = problems
            .Where(entry => entry.AgentId == item.AgentId
                            && entry.Kind == NhAssistantRunConfigurationProblems.ConsultationKind
                            && entry.Id == item.SubjectType)
            .Select(entry => entry.Code)
            .ToArray();
        // What is in effect of it: the options of the configuration in effect, with the parts that no
        // longer fit dropped or capped.
        var inEffect = registration.AgentRuns.TryGetValue(item.AgentId, out var runOptions)
            ? runOptions.Consultations.FirstOrDefault(consultation => consultation.IsData
                && string.Equals(consultation.SubjectType, item.SubjectType, StringComparison.Ordinal))
            : null;
        var consultToolId = NhAiSubjectRef.IsValidType(item.SubjectType)
            ? NhAssistantConsultationTools.ConsultToolId(item.AgentId, item.SubjectType)
            : string.Empty;
        // A stored selection that no longer reads keeps the consultation out of effect as invalid.
        var toolSelectors = NhAssistantDataConsultationToolSelectors.TryRead(item.ToolSelectorsJson, out var stored) ? stored : null;
        return new NhAssistantDataConsultationDto(
            item.SubjectType,
            item.Version,
            item.Description,
            item.Instructions,
            item.InstructionsHash,
            item.MinimumRole,
            item.AskerPolicy,
            NhAssistantDataConsultationRoutes.Read(item.RoutesJson) ?? [],
            item.ProposalRole,
            item.ProposalPolicyId,
            item.MaxToolCalls,
            item.TimeoutSeconds,
            item.MaxAnswerChars,
            item.MaxHistoryMessages,
            item.AllowRequests,
            item.ExposeToMcp,
            item.DataClassification,
            item.UpdatedAt,
            item.UpdatedBy,
            item.Reason,
            inEffect is not null,
            own.FirstOrDefault())
        {
            Problems = own,
            RoutesInEffect = inEffect?.AllowedRoutes ?? [],
            ToolSelectors = toolSelectors,
            // Out of effect, the consultation offers nothing.
            ToolSelectorsInEffect = inEffect is null ? [] : inEffect.ToolSelectors,
            ConsultToolId = consultToolId,
            RequestToolId = inEffect is { AllowRequests: true } ? inEffect.RequestToolId : null,
            AskingAgents = consultToolId.Length == 0 ? [] : AskersOf(item.AgentId, consultToolId, askers),
            CreatedAt = item.CreatedAt,
            CreatedBy = item.CreatedBy
        };
    }

    /// <summary>
    /// The warnings of a data skill on its agent: its classification, or else the agent's, that
    /// neither its profile, or else the agent's, nor any of the profile's fallbacks permits.
    /// </summary>
    private IReadOnlyList<string> WarningsOf(AssistantAgentSkillDefinition skill, NhAssistantAgent agent)
    {
        var classification = NhAssistantDataClassificationNames.TryParse(skill.DataClassification, out var own)
            ? own
            : agent.DataClassification;
        return NhAssistantClassificationChecks.WarningsOf(
            services.GetService<INhAiModelProfileRegistry>(),
            skill.ProfileName ?? agent.Definition.ProfileName,
            classification);
    }

    private async Task<IReadOnlyList<string>> WarningsOfAsync(AssistantAgentSkillDefinition skill, CancellationToken cancellationToken)
    {
        var agent = await agents.FindAsync(skill.AgentId, includeDisabled: true, cancellationToken);
        return agent is null ? [] : WarningsOf(skill, agent);
    }

    /// <summary>
    /// The agent that data skills and triggers are added to: one whose runs are configured in code,
    /// or one created in the administration with run settings.
    /// </summary>
    private async Task<(TaskResult? Refused, NhAssistantRunAgent? Agent)> ResolveAgentAsync(string agentId, CancellationToken cancellationToken)
    {
        var agent = await agents.FindAsync(agentId, includeDisabled: true, cancellationToken);
        if (agent is null)
        {
            return (TaskResult.Failed(NhAssistantErrorCodes.AgentNotFound, "The agent was not found."), null);
        }
        if (validator.CodeAgent(agentId) is { } codeAgent)
        {
            return (null, codeAgent);
        }
        if (agent.Source == NhAssistantAgentSources.Admin && await store.FindRunSettingsAsync(agentId, cancellationToken) is not null)
        {
            return (null, new NhAssistantRunAgent([], agent.Definition.ToolSelectors));
        }

        return (TaskResult.Failed(NhAssistantErrorCodes.AgentNotConfigured, "The agent has no run configuration."), null);
    }

    /// <summary>
    /// Whether the account exists and may sign in, as the principal factory that rebuilds it for the
    /// agent's runs decides.
    /// </summary>
    private async Task<bool> AccountExistsAsync(string agentId, string accountActorId, CancellationToken cancellationToken)
    {
        if (services.GetService<INhAiPrincipalFactory>() is not { } principals)
        {
            return false;
        }

        var subject = new NhAiSubjectInfo(new NhAiSubjectRef("agent", agentId), NhAiSubjectInfo.CreateScopeKey(null, null));
        return await principals.CreateAsync(accountActorId, subject, cancellationToken) is not null;
    }

    private async Task<IReadOnlyDictionary<string, AssistantAgentSkillDefinition>> LiveSkillsAsync(string agentId, CancellationToken cancellationToken)
    {
        return (await store.ListSkillsAsync(agentId, cancellationToken))
            .Where(item => item.Status == NhAssistantSkillDefinitionStatuses.Live)
            .ToDictionary(item => item.SkillId, StringComparer.Ordinal);
    }

    private static TaskResult? ValidateChange(string actorId, ref string? reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        if (actorId.Length > MaxActorIdLength)
        {
            throw new ArgumentOutOfRangeException(nameof(actorId), $"An actor id has at most {MaxActorIdLength} characters.");
        }

        reason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        return reason?.Length > NhAssistantLimits.MaxStateReasonCharacters
            ? NhAssistantValidationErrors.Failed("reason", NhAssistantFieldErrors.TooLong)
            : null;
    }

    /// <summary>
    /// The lower file limits of an agent or a skill (D11): each from 1 to the application's limit, and
    /// only next to <c>acceptsFiles</c>, because an agent or skill that accepts no files has none.
    /// </summary>
    private void RequireFileLimits(
        NhAssistantValidationErrors errors,
        bool acceptsFiles,
        int? maxFileBytes,
        int? maxFilePages,
        int? maxFilesPerTurn,
        int? maxFileTokensPerTurn)
    {
        var limits = registration.Limits;
        RequireFileLimit(errors, "maxFileBytes", acceptsFiles, maxFileBytes, limits.MaxFileBytes);
        RequireFileLimit(errors, "maxFilePages", acceptsFiles, maxFilePages, limits.MaxFilePages);
        RequireFileLimit(errors, "maxFilesPerTurn", acceptsFiles, maxFilesPerTurn, limits.MaxFilesPerTurn);
        RequireFileLimit(errors, "maxFileTokensPerTurn", acceptsFiles, maxFileTokensPerTurn, limits.MaxFileTokensPerTurn);
    }

    private static void RequireFileLimit(NhAssistantValidationErrors errors, string field, bool acceptsFiles, int? value, int applicationLimit)
    {
        if (value is not { } limit)
        {
            return;
        }

        errors.Require(acceptsFiles && limit >= 1 && limit <= applicationLimit, field, NhAssistantFieldErrors.Invalid);
    }

    private static TaskResult SkillFieldError(string problem)
    {
        return problem switch
        {
            NhAssistantRunConfigurationProblems.ToolNotCovered => NhAssistantValidationErrors.Failed("tools", NhAssistantFieldErrors.Invalid),
            NhAssistantRunConfigurationProblems.UnknownProfile => NhAssistantValidationErrors.Failed("profile", NhAssistantFieldErrors.NotFound),
            NhAssistantRunConfigurationProblems.UnknownPolicy => NhAssistantValidationErrors.Failed("approvalPolicy", NhAssistantFieldErrors.NotFound),
            NhAssistantRunConfigurationProblems.UnknownRuleHandler => NhAssistantValidationErrors.Failed("ruleHandler", NhAssistantFieldErrors.NotFound),
            _ => NhAssistantValidationErrors.Failed("skillId", NhAssistantFieldErrors.Invalid)
        };
    }

    private static TaskResult TriggerFieldError(string problem, bool schedule)
    {
        return problem switch
        {
            NhAssistantRunConfigurationProblems.UnknownSkill => NhAssistantValidationErrors.Failed("skill", NhAssistantFieldErrors.NotFound),
            NhAssistantRunConfigurationProblems.UnknownEvent => NhAssistantValidationErrors.Failed("event", NhAssistantFieldErrors.NotFound),
            // A schedule without a subject set runs for the whole application, which the agent does
            // not work on: it needs a subject set.
            NhAssistantRunConfigurationProblems.NotWorkedOn when schedule => NhAssistantValidationErrors.Failed("subjectSet", NhAssistantFieldErrors.Required),
            // The event names a subject type the agent does not work on.
            NhAssistantRunConfigurationProblems.NotWorkedOn => NhAssistantValidationErrors.Failed("event", NhAssistantFieldErrors.Invalid),
            NhAssistantRunConfigurationProblems.UnknownSubjectSet => NhAssistantValidationErrors.Failed("subjectSet", NhAssistantFieldErrors.NotFound),
            NhAssistantRunConfigurationProblems.InvalidSchedule => NhAssistantValidationErrors.Failed("cron", NhAssistantFieldErrors.Invalid),
            // A filter the event does not register: its own code, so an import can tell it apart.
            NhAssistantRunConfigurationProblems.UnknownFilter => new NhAssistantValidationErrors()
                .Add("filters", NhAssistantFieldErrors.NotFound)
                .ToResult(NhAssistantErrorCodes.TriggerFilterNotFound, "The trigger event has no such filter."),
            _ => NhAssistantValidationErrors.Failed("triggerId", NhAssistantFieldErrors.Invalid)
        };
    }

    private static string? Blank(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static string SourceName(NhAssistantTriggerSource source)
    {
        return source switch
        {
            NhAssistantTriggerSource.Schedule => NhAssistantDataTriggerSources.Schedule,
            NhAssistantTriggerSource.Webhook => "webhook",
            _ => NhAssistantDataTriggerSources.Event
        };
    }

    private static NhAssistantAgentRunSettingsDto ToDto(
        AssistantAgentRunConfiguration item,
        IReadOnlyList<NhAssistantRunConfigurationProblem> problems)
    {
        var problem = problems.FirstOrDefault(entry => entry.AgentId == item.AgentId
                                                       && entry.Kind == NhAssistantRunConfigurationProblems.AgentKind)?.Code;
        return new NhAssistantAgentRunSettingsDto(
            item.Version,
            item.Accountability,
            item.ServiceAccountActorId,
            item.DelegateFromInitiator,
            item.AllowGatewayReads,
            item.ElevatedReads,
            item.ApprovalPolicyId,
            item.RetryCount,
            item.DailyToolCalls,
            item.DailyTokens,
            item.MaxConcurrentRuns,
            item.MaxRunsPerSubjectPerDay,
            item.MaxTokensPerRun,
            item.TriageProfileName,
            item.RuleHandlerName,
            item.UpdatedAt,
            item.UpdatedBy,
            item.Reason,
            problem is null,
            problem)
        {
            AcceptsFiles = item.AcceptsFiles,
            MaxFileBytes = item.MaxFileBytes,
            MaxFilePages = item.MaxFilePages,
            MaxFilesPerTurn = item.MaxFilesPerTurn,
            MaxFileTokensPerTurn = item.MaxFileTokensPerTurn
        };
    }

    private static NhAssistantDataSkillDto ToDto(
        AssistantAgentSkillDefinition item,
        IReadOnlyList<NhAssistantRunConfigurationProblem> problems,
        string kind = NhAssistantRunConfigurationProblems.SkillKind)
    {
        var problem = problems.FirstOrDefault(entry => entry.AgentId == item.AgentId
                                                       && entry.Kind == kind
                                                       && entry.Id == item.SkillId)?.Code;
        return new NhAssistantDataSkillDto(
            item.SkillId,
            item.Version,
            item.Description,
            item.Instructions,
            item.InstructionsHash,
            NhAssistantRunConfigurations.ReadSelectors(item.ToolSelectorsJson) ?? [],
            item.ProfileName,
            item.RuleHandlerName,
            item.MaxTokensPerRun,
            item.DailyToolCalls,
            item.DailyTokens,
            item.ApprovalPolicyId,
            item.CreatedAt,
            item.CreatedBy,
            item.Reason,
            problem is null,
            problem)
        {
            DataClassification = item.DataClassification,
            AcceptsFiles = item.AcceptsFiles,
            MaxFileBytes = item.MaxFileBytes,
            MaxFilePages = item.MaxFilePages,
            MaxFilesPerTurn = item.MaxFilesPerTurn,
            MaxFileTokensPerTurn = item.MaxFileTokensPerTurn,
            InstructionsAsset = NhAssistantInstructionAssets.ReferenceOf(item)
        };
    }

    private static NhAssistantDataTriggerDto ToDto(
        AssistantAgentTriggerDefinition item,
        IReadOnlyList<NhAssistantRunConfigurationProblem> problems)
    {
        var problem = problems.FirstOrDefault(entry => entry.AgentId == item.AgentId
                                                       && entry.Kind == NhAssistantRunConfigurationProblems.TriggerKind
                                                       && entry.Id == item.Id)?.Code;
        return new NhAssistantDataTriggerDto(
            item.Id,
            item.SkillId,
            item.Source,
            item.EventName,
            item.Cron,
            item.TimeZone,
            item.SubjectSetName,
            item.Message,
            item.Conflict,
            item.DebounceSeconds,
            item.Shadow,
            item.Version,
            item.UpdatedAt,
            item.UpdatedBy,
            item.Reason,
            problem is null,
            problem)
        {
            Filters = NhAssistantDataTriggerFilters.Read(item.FiltersJson) ?? []
        };
    }
}
