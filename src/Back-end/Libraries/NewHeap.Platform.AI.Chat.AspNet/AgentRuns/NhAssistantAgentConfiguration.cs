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
    /// is <see langword="false"/>, active, and without run settings until
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
    /// Fails with <c>assistant-run-settings-in-use</c> while it has data skills or data triggers.
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
    /// Adds a data skill, which starts in shadow, or stores the next version of one. With
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
    /// Adds a data trigger, which starts in shadow, or changes one in the mode it has.
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
}

/// <param name="TriggerEvents">The events a data trigger can start a skill on.</param>
/// <param name="SubjectSets">The subjects a data schedule can start a skill for.</param>
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
    /// The subject types the application registered with <c>AddSubjectType</c>, ordered by id, with
    /// their names in the request's language.
    /// </summary>
    public IReadOnlyList<NhAssistantSubjectTypeDto> SubjectTypes { get; init; } = [];

    /// <summary>
    /// The policies the application lists with <c>AddAgentPolicy</c>, ordered by name: what an
    /// administrator may choose as the run and view policy of an agent created in the administration.
    /// </summary>
    public IReadOnlyList<NhAssistantRunBlockDto> AgentPolicies { get; init; } = [];

    /// <summary>
    /// Whether the application lists its divisions, so an agent may work per division; without it the
    /// administration does not offer the choice.
    /// </summary>
    public bool Divisions { get; init; }
}

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
    string? ViewPolicy = null);

/// <param name="Name">The building block's name.</param>
/// <param name="Description">What it means, for the administration.</param>
public sealed record NhAssistantRunBlockDto(string Name, string Description)
{
    /// <summary>
    /// For a trigger event that names its subject type up front, that type; data triggers on it fit
    /// only agents that work on it. <see langword="null"/> when the type is known only when it fires.
    /// </summary>
    public string? SubjectType { get; init; }
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
    NhAssistantAgentRunSettingsDto? RunSettings);

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
    string? Problem);

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
    string? Reason = null);

public sealed record NhAssistantCodeSkillDto(string SkillId, string Description);

/// <param name="Source"><c>event</c>, <c>schedule</c> or <c>webhook</c>.</param>
public sealed record NhAssistantCodeTriggerDto(string TriggerId, string Source, string? SkillId);

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
    string? Reason);

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
    string? Problem);

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
    string? Apply = null);

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
/// <param name="SubjectSet">For a schedule: a subject set registered with <c>AddSubjectSet</c>.</param>
/// <param name="Message">For a schedule: what every run receives, at most 2,000 characters.</param>
/// <param name="Conflict">What happens while a run on the subject is active: <c>coalesce</c>,
/// <c>queue</c>, <c>supersede</c> or <c>ignore</c>. Events coalesce and schedules ignore by
/// default.</param>
/// <param name="DebounceSeconds">For an event trigger: the window in which events coalesce, at most
/// 3,600; 120 by default.</param>
/// <param name="ExpectedVersion">The trigger's version the change was made on;
/// <see langword="null"/> for a new trigger.</param>
/// <param name="Reason">At most 500 characters.</param>
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
    string? Reason = null);

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
                .Select(item => new NhAssistantRunBlockDto(item.Name, item.Description) { SubjectType = item.SubjectType })
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
            Divisions = services.GetRequiredService<NhAssistantWorksOnRule>().DivisionsListed
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
                    request.ViewPolicy)),
            actorId,
            cancellationToken);
        if (!created.Success)
        {
            return TaskResult<NhAssistantAgentConfigurationDto>.Failed(created);
        }

        return TaskResult<NhAssistantAgentConfigurationDto>.Succeeded((await GetAsync(request.Id, cancellationToken))!);
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
                Candidate = versions
                    .Where(candidate => candidate.SkillId == item.SkillId && candidate.Status == NhAssistantSkillDefinitionStatuses.Candidate)
                    .Select(candidate => ToDto(candidate, problems, NhAssistantRunConfigurationProblems.CandidateKind))
                    .FirstOrDefault()
            })
            .ToArray();
        var triggers = (await store.ListTriggersAsync(agentId, cancellationToken))
            .Select(item => ToDto(item, problems))
            .ToArray();
        return new NhAssistantAgentConfigurationDto(
            agentId,
            agent.Definition.ToolSelectors,
            code is null ? [] : code.Skills.Values.Select(skill => new NhAssistantCodeSkillDto(skill.Id, skill.Description)).ToArray(),
            skills,
            registration.CodeTriggers.Values
                .Where(trigger => trigger.AgentId == agentId)
                .OrderBy(trigger => trigger.Id, StringComparer.Ordinal)
                .Select(trigger => new NhAssistantCodeTriggerDto(trigger.Id, SourceName(trigger.Source), trigger.SkillId))
                .ToArray(),
            triggers,
            problems,
            code is null ? NhAssistantAgentSources.Admin : NhAssistantAgentSources.Code,
            settings is null ? null : ToDto(settings, problems));
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
                return TaskResult.Failed(NhAssistantErrorCodes.RunSettingsInUse, "Data skills or triggers still belong to the agent.");
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
            .Select(item => new NhAssistantDataSkillVersionDto(item.Version, item.Status, item.Description, item.InstructionsHash, item.CreatedAt, item.CreatedBy, item.Reason))
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
        var tools = (request.Tools ?? []).Select(tool => tool?.Trim() ?? string.Empty).Distinct(StringComparer.Ordinal).ToArray();
        var errors = new NhAssistantValidationErrors()
            .Require(NhAiSubjectRef.IsValidType(skillId), "skillId", NhAssistantFieldErrors.Invalid)
            .Require(!string.IsNullOrEmpty(description), "description", NhAssistantFieldErrors.Required)
            .Require(description is null || description.Length <= NhAssistantSkillBuilder.MaxDescriptionLength, "description", NhAssistantFieldErrors.TooLong)
            .Require(instructions is null || instructions.Length <= NhAssistantAgentConfigurationLimits.MaxSkillInstructionsLength, "instructions", NhAssistantFieldErrors.TooLong)
            .Require(request.MaxTokensPerRun is null or >= 1, "maxTokensPerRun", NhAssistantFieldErrors.Invalid)
            .Require((request.DailyToolCalls is null) == (request.DailyTokens is null) && request.DailyToolCalls is null or >= 1 && request.DailyTokens is null or >= 1, "dailyBudget", NhAssistantFieldErrors.Invalid)
            .Require(request.Apply is null or NhAssistantSkillApplyModes.Live or NhAssistantSkillApplyModes.Shadow, "apply", NhAssistantFieldErrors.Invalid)
            // A new skill has no live version to evaluate a candidate next to; it starts in shadow.
            .Require(request.Apply != NhAssistantSkillApplyModes.Shadow || request.ExpectedVersion is not null, "apply", NhAssistantFieldErrors.Invalid);
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
            InstructionsHash = instructions is null ? null : NhAssistantAgentCatalog.ComputeHash(instructions),
            ToolSelectorsJson = JsonSerializer.Serialize(tools),
            ProfileName = Blank(request.Profile),
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

        await events.SkillDefinitionChangedAsync(
            agentId,
            skillId,
            saved.Value!.Version,
            saved.Value.InstructionsHash,
            asCandidate ? "candidate" : saved.Value.Version == 1 || request.ExpectedVersion is null ? "created" : "changed",
            actorId,
            cancellationToken);
        await configurations.ReloadAsync(cancellationToken);
        return TaskResult<NhAssistantDataSkillDto>.Succeeded(asCandidate
            ? ToDto(saved.Value, configurations.Problems, NhAssistantRunConfigurationProblems.CandidateKind)
            : ToDto(saved.Value, configurations.Problems));
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
        return changed.Failure is { } failure
            ? TaskResult<NhAssistantDataSkillDto>.Failed(failure)
            : TaskResult<NhAssistantDataSkillDto>.Succeeded(ToDto(changed.Definition!, configurations.Problems));
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
                group.Count(item => liveIds.Contains(item.RunId) && item.Status is NhAssistantApprovalStatuses.Approved or NhAssistantApprovalStatuses.Executed),
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
            cancellationToken);
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

        await events.SkillDefinitionChangedAsync(agentId, skillId, removed.Value!.Version, removed.Value.InstructionsHash, "removed", actorId, cancellationToken);
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
        var errors = new NhAssistantValidationErrors()
            .Require(NhAiSubjectRef.IsValidType(triggerId) && !NhAssistantTriggerIds.IsReserved(triggerId), "triggerId", NhAssistantFieldErrors.Invalid)
            .Require(request.Source is NhAssistantDataTriggerSources.Event or NhAssistantDataTriggerSources.Schedule, "source", request.Source is null ? NhAssistantFieldErrors.Required : NhAssistantFieldErrors.Invalid)
            .Require(NhAssistantTriggerConflictNames.TryParse(conflict, out _), "conflict", NhAssistantFieldErrors.Invalid)
            .Require(request.DebounceSeconds is null or >= 0 and <= NhAssistantAgentConfigurationLimits.MaxDebounceSeconds, "debounceSeconds", NhAssistantFieldErrors.Invalid);
        if (request.Source == NhAssistantDataTriggerSources.Event)
        {
            errors.Require(!string.IsNullOrWhiteSpace(request.Event), "event", NhAssistantFieldErrors.Required);
        }
        if (schedule)
        {
            errors
                .Require(!string.IsNullOrWhiteSpace(request.Cron), "cron", NhAssistantFieldErrors.Required)
                .Require(!string.IsNullOrWhiteSpace(request.SubjectSet), "subjectSet", NhAssistantFieldErrors.Required)
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
            return TaskResult<NhAssistantDataTriggerDto>.Failed(TriggerFieldError(problem));
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

    private static TaskResult TriggerFieldError(string problem)
    {
        return problem switch
        {
            NhAssistantRunConfigurationProblems.UnknownSkill => NhAssistantValidationErrors.Failed("skill", NhAssistantFieldErrors.NotFound),
            NhAssistantRunConfigurationProblems.UnknownEvent => NhAssistantValidationErrors.Failed("event", NhAssistantFieldErrors.NotFound),
            // The event names a subject type the agent does not work on.
            NhAssistantRunConfigurationProblems.NotWorkedOn => NhAssistantValidationErrors.Failed("event", NhAssistantFieldErrors.Invalid),
            NhAssistantRunConfigurationProblems.UnknownSubjectSet => NhAssistantValidationErrors.Failed("subjectSet", NhAssistantFieldErrors.NotFound),
            NhAssistantRunConfigurationProblems.InvalidSchedule => NhAssistantValidationErrors.Failed("cron", NhAssistantFieldErrors.Invalid),
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
            problem);
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
            problem);
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
            problem);
    }
}
