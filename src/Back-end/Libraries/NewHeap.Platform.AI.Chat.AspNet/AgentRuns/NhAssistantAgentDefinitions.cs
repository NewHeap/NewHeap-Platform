using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.Common.Models;

namespace NewHeap.Platform.AI.Chat.AspNet.AgentRuns;

/// <summary>
/// Changes the definitions of agents from code, as the administration's <c>PUT</c>,
/// <c>POST agents/{id}/reset</c> and <c>DELETE admin/agents/{id}</c> do: agents created at runtime and
/// overrides of agents in code, with the same checks, versions, package locks and audit. It also
/// promotes a shadow variant created as data onto its live agent. It authorizes nothing itself: the
/// application calls it for an administrator or a process it authorized, and passes their actor id.
/// <c>INhAssistantAgentConfiguration.CreateAgentAsync</c> creates agents; read them as they are in
/// effect with <see cref="INhAssistantAgentDirectory"/>.
/// </summary>
public interface INhAssistantAgentDefinitions
{
    /// <summary>
    /// Changes an agent's definition when <paramref name="expectedVersion"/> is its version, and makes
    /// its next version. For an agent created at runtime every field of
    /// <paramref name="request"/> applies; for an agent in code its display name, description,
    /// instructions, tool selectors, required policy and autonomy override the code until
    /// <see cref="ResetAsync"/>, and a definition equal to the code is no override. The agent keeps its
    /// MCP assignments unless <see cref="NhAssistantAgentDefinitionRequest.McpServerIds"/> names them.
    /// Fails with <c>assistant-agent-not-found</c>, <c>assistant-version-conflict</c>, field errors
    /// (<c>requiredPolicy: not-found</c> for a policy the application does not know, <c>metadata:
    /// not-allowed</c> and <c>instructionsAsset: not-allowed</c> for an agent in code, and the checks
    /// of the administration), <c>{field}: locked</c> or <c>assistant-package-locked</c> for a field an
    /// agent package locks, and <c>assistant-mcp-server-not-found</c>. Audited as
    /// <c>AdminAgentUpdated</c>, with <c>AdminAgentMetadataChanged</c> per metadata entry it changes.
    /// </summary>
    Task<TaskResult<NhAssistantAgentInEffectDto>> UpdateAsync(
        string agentId,
        NhAssistantAgentDefinitionRequest request,
        int expectedVersion,
        string actorId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Restores an agent in code to its code definition and removes its MCP assignments; its run state
    /// stays as it is. Fails with <c>assistant-agent-not-found</c>, <c>assistant-agent-not-code</c> for
    /// an agent created at runtime and <c>assistant-package-locked</c> when a package locks a field the
    /// reset would change. Audited as <c>AdminAgentReset</c>.
    /// </summary>
    Task<TaskResult<NhAssistantAgentInEffectDto>> ResetAsync(
        string agentId,
        string actorId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes an agent created at runtime with its run settings, skills, triggers and metadata. An
    /// agent in code cannot be deleted (<c>assistant-code-agent-not-deletable</c>); switch it off
    /// instead. An agent that belongs to an agent package stays until it is unlinked
    /// (<c>assistant-package-locked</c>). Audited as <c>AdminAgentDeleted</c>.
    /// </summary>
    Task<TaskResult> DeleteAsync(string agentId, string actorId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Promotes a shadow variant created as data onto its live agent: the variant's definition becomes
    /// the live agent's next version through <see cref="UpdateAsync"/>, with its checks, version and
    /// audit; with <see cref="NhAssistantShadowVariantPromotionRequest.MoveSkills"/> the variant's data
    /// skills become versions of the live agent's skills of the same id; then the variant is deleted.
    /// The variant never becomes live itself. The live agent keeps its display name, MCP assignments,
    /// metadata, run settings, triggers, state and shadow mode; an agent in code takes only the fields
    /// it can override. Fails with <c>assistant-agent-not-found</c>, <c>assistant-not-shadow-variant</c>
    /// for an agent that is no variant created as data, <c>assistant-shadow-variants-not-allowed</c>
    /// when the live agent may no longer have data variants, <c>assistant-version-conflict</c> for a
    /// stale version of the live agent or the variant, <c>assistant-skill-in-code</c> for a skill the
    /// live agent has in code, and the failures of the update, the skill saves and the deletion. The
    /// steps run in that order and a failure stops there: the variant stays until the last step.
    /// Audited as <c>AdminAgentShadowVariantPromoted</c> after the events of its steps.
    /// <para>
    /// With <see cref="NhAssistantShadowVariantPromotionRequest.AsPackageVersion"/> (FB11) a variant
    /// whose every part is linked to one agent package becomes that package's next version of the live
    /// agent: its parts apply to the live agent with the import's own rules (its definition without the
    /// display name, chat offer and live agent of a variant, its run settings, skills, triggers,
    /// consultations, hand-offs and the metadata entries the package carries, with the package's locks),
    /// the live agent's parts are linked to the variant's package id and version, the live agent leaves
    /// the package it belonged to before, and the variant is deleted with its links, all in one
    /// transaction. An agent in code takes the fields it can override and keeps its run settings in
    /// code. It fails, changing nothing, with <c>assistant-package-not-linked</c> when a part of the
    /// variant is not linked, <c>moveSkills: not-allowed</c> together with
    /// <see cref="NhAssistantShadowVariantPromotionRequest.MoveSkills"/>, and the import's
    /// <c>assistant-package-refused</c> (<c>parts: invalid</c> with <c>parts.{part}.{field}</c>) when
    /// the import would refuse a part. Audited once as <c>AdminAgentShadowVariantPromoted</c> with the
    /// result <c>promoted-as-package-version</c> and the package's id, version and hash, after the
    /// change events of the parts it wrote.
    /// </para>
    /// </summary>
    Task<TaskResult<NhAssistantShadowVariantPromotionDto>> PromoteShadowVariantAsync(
        string variantId,
        NhAssistantShadowVariantPromotionRequest request,
        string actorId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// A change of an agent's definition with <see cref="INhAssistantAgentDefinitions.UpdateAsync"/>.
/// The display name, description, instructions, tool selectors, autonomy and required policy replace
/// the agent's; every other field is optional, and <see langword="null"/> keeps what the agent has.
/// </summary>
/// <param name="DisplayName">The name people see, at most 256 characters; for an agent in code usually
/// a key of the application's translations.</param>
/// <param name="Description">What the agent does, at most 512 characters.</param>
/// <param name="Instructions">The instructions, at most 20,000 characters; empty when
/// <see cref="InstructionsAsset"/> names an asset.</param>
/// <param name="ToolSelectors">The tools the agent may use, by id or <c>prefix.*</c> selector.</param>
/// <param name="Autonomy">How far the agent may act on its own.</param>
/// <param name="RequiredPolicy">The policy a person must pass to use the agent, one the application
/// registered (<c>requiredPolicy: not-found</c> otherwise); <see langword="null"/> for none.</param>
public sealed record NhAssistantAgentDefinitionRequest(
    string DisplayName,
    string Description,
    string Instructions,
    IReadOnlyList<string> ToolSelectors,
    NhAiAutonomyLevel Autonomy,
    string? RequiredPolicy = null)
{
    /// <summary>
    /// The MCP servers the agent may use, replacing its assignments; <see langword="null"/> keeps them.
    /// </summary>
    public IReadOnlyList<string>? McpServerIds { get; init; }

    /// <summary>
    /// For an agent created at runtime: whether people can choose it in the chat;
    /// <see langword="null"/> keeps it. A shadow variant is never offered.
    /// </summary>
    public bool? OfferedInChat { get; init; }

    /// <summary>
    /// For an agent created at runtime: the object types it works on. What the agent works on
    /// (<see cref="WorksOnSubjectTypes"/>, <see cref="WorksOnAllSubjects"/>,
    /// <see cref="WorksOnApplication"/>, <see cref="PerDivision"/>, <see cref="RunPolicy"/> and
    /// <see cref="ViewPolicy"/>) is sent together, as in <see cref="NhAssistantAgentRequest"/>: all six
    /// <see langword="null"/> keep it.
    /// </summary>
    public IReadOnlyList<string>? WorksOnSubjectTypes { get; init; }

    /// <summary>For an agent created at runtime: it works on every object type.</summary>
    public bool? WorksOnAllSubjects { get; init; }

    /// <summary>For an agent created at runtime: it works on the whole application.</summary>
    public bool? WorksOnApplication { get; init; }

    /// <summary>For an agent created at runtime: it works per division.</summary>
    public bool? PerDivision { get; init; }

    /// <summary>For an agent created at runtime: who may run it for the whole application.</summary>
    public string? RunPolicy { get; init; }

    /// <summary>For an agent created at runtime: who sees its results for the whole application.</summary>
    public string? ViewPolicy { get; init; }

    /// <summary>
    /// For an agent created at runtime: the model profile of its model calls, one of the run catalog's
    /// profiles; <see langword="null"/> keeps it.
    /// </summary>
    public string? Profile { get; init; }

    /// <summary>
    /// For an agent created at runtime: the classification of the data its model calls carry;
    /// <see langword="null"/> keeps it.
    /// </summary>
    public string? DataClassification { get; init; }

    /// <summary>
    /// For an agent created at runtime: values of entries of agent metadata by name, saved with the
    /// definition's version; a JSON <c>null</c> removes one and entries left out keep theirs. An agent in
    /// code changes its editable entries one by one (<c>metadata: not-allowed</c>).
    /// </summary>
    public IReadOnlyDictionary<string, JsonElement>? Metadata { get; init; }

    /// <summary>
    /// For an agent created at runtime: the asset of the application its instructions come from, instead
    /// of <see cref="Instructions"/>; <see langword="null"/> makes or keeps them text.
    /// </summary>
    public NhAssistantInstructionAssetReference? InstructionsAsset { get; init; }
}

/// <summary>
/// The promotion of a shadow variant created as data onto its live agent with
/// <see cref="INhAssistantAgentDefinitions.PromoteShadowVariantAsync"/>.
/// </summary>
/// <param name="ExpectedVersion">The live agent's version the promotion was decided on.</param>
/// <param name="MoveSkills">The variant's data skills become versions of the live agent's skills with
/// the same id: the next live version of an existing data skill, or a new data skill, which starts in
/// shadow as every new skill does. Without it the variant's skills are deleted with the variant.</param>
/// <param name="ExpectedVariantVersion">The variant's version that was evaluated;
/// <see langword="null"/> skips the check.</param>
/// <param name="Reason">Why, at most 500 characters, stored with the skill versions it saves.</param>
public sealed record NhAssistantShadowVariantPromotionRequest(
    int ExpectedVersion,
    bool MoveSkills = false,
    int? ExpectedVariantVersion = null,
    string? Reason = null)
{
    /// <summary>
    /// The variant's parts, every one linked to one agent package, become that package's next version
    /// of the live agent with the import's rules, in one transaction (FB11); see
    /// <see cref="INhAssistantAgentDefinitions.PromoteShadowVariantAsync"/>. Off by default: the
    /// promotion goes through the update path.
    /// </summary>
    public bool AsPackageVersion { get; init; }
}

/// <summary>
/// The outcome of a promotion of a shadow variant.
/// </summary>
/// <param name="Agent">The live agent as it is in effect now.</param>
/// <param name="VariantId">The variant that was deleted.</param>
/// <param name="MovedSkills">The ids of the skills that moved to the live agent, ordered; empty
/// without <see cref="NhAssistantShadowVariantPromotionRequest.MoveSkills"/>.</param>
public sealed record NhAssistantShadowVariantPromotionDto(
    NhAssistantAgentInEffectDto Agent,
    string VariantId,
    IReadOnlyList<string> MovedSkills)
{
    /// <summary>
    /// For a promotion as the package's version
    /// (<see cref="NhAssistantShadowVariantPromotionRequest.AsPackageVersion"/>): the package the live
    /// agent is linked to now, with its version, hash and the outcome of every part as an import reports
    /// it; <see langword="null"/> otherwise.
    /// </summary>
    public NhAssistantAgentPackageImportDto? Package { get; init; }
}

internal sealed class NhAssistantAgentDefinitions(
    NhAssistantAgentAdministration administration,
    NhAssistantAgentCatalog catalog,
    NhAssistantRegistrationState registration,
    NhAssistantRunConfigurations configurations,
    NhAssistantRunConfigurationValidator validator,
    NhAssistantPackageLockCheck locks,
    NhAssistantAgentDirectory directory,
    IEnumerable<INhAssistantBusinessAuditSink> auditSinks,
    IServiceProvider services) : INhAssistantAgentDefinitions
{
    private const int MaxActorIdLength = 256;
    private const int MaxReasonLength = 500;
    private readonly IReadOnlyList<INhAssistantBusinessAuditSink> _auditSinks = auditSinks.ToArray();

    public async Task<TaskResult<NhAssistantAgentInEffectDto>> UpdateAsync(
        string agentId,
        NhAssistantAgentDefinitionRequest request,
        int expectedVersion,
        string actorId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ArgumentNullException.ThrowIfNull(request);
        ValidateActor(actorId);

        // The checks of the administration's endpoint before its input reaches the administration.
        var errors = new NhAssistantValidationErrors()
            .Require(request.DisplayName is not null, "displayName", NhAssistantFieldErrors.Required)
            .Require(request.Description is not null, "description", NhAssistantFieldErrors.Required)
            .Require(request.Instructions is not null || request.InstructionsAsset is not null, "instructions", NhAssistantFieldErrors.Required)
            .Require(request.ToolSelectors is not null, "toolSelectors", NhAssistantFieldErrors.Required);
        var requiredPolicy = string.IsNullOrWhiteSpace(request.RequiredPolicy) ? null : request.RequiredPolicy;
        if (requiredPolicy is not null
            && await services.GetRequiredService<IAuthorizationPolicyProvider>().GetPolicyAsync(requiredPolicy) is null)
        {
            errors.Add("requiredPolicy", NhAssistantFieldErrors.NotFound);
        }
        if (!errors.IsEmpty)
        {
            return TaskResult<NhAssistantAgentInEffectDto>.Failed(errors.ToResult());
        }

        var input = new NhAssistantAgentInput(
            agentId,
            request.DisplayName,
            request.Description,
            request.Instructions ?? string.Empty,
            request.ToolSelectors,
            request.McpServerIds ?? [],
            requiredPolicy,
            request.Autonomy,
            request.OfferedInChat,
            NhAssistantAgentWorksOn.FromInput(
                request.WorksOnSubjectTypes,
                request.WorksOnAllSubjects,
                request.WorksOnApplication,
                request.PerDivision,
                request.RunPolicy,
                request.ViewPolicy),
            StartsOff: false,
            string.IsNullOrWhiteSpace(request.Profile) ? null : request.Profile.Trim(),
            string.IsNullOrWhiteSpace(request.DataClassification) ? null : request.DataClassification.Trim(),
            request.Metadata,
            request.InstructionsAsset)
        {
            // As at the endpoint, a request that names no servers keeps them.
            KeepMcpServers = request.McpServerIds is null
        };
        return await InEffectAsync(await administration.UpdateAsync(input, expectedVersion, actorId, cancellationToken), cancellationToken);
    }

    public async Task<TaskResult<NhAssistantAgentInEffectDto>> ResetAsync(
        string agentId,
        string actorId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ValidateActor(actorId);
        return await InEffectAsync(await administration.ResetAsync(agentId, actorId, cancellationToken), cancellationToken);
    }

    public async Task<TaskResult> DeleteAsync(string agentId, string actorId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ValidateActor(actorId);
        return await administration.DeleteAsync(agentId, actorId, cancellationToken);
    }

    public async Task<TaskResult<NhAssistantShadowVariantPromotionDto>> PromoteShadowVariantAsync(
        string variantId,
        NhAssistantShadowVariantPromotionRequest request,
        string actorId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(variantId);
        ArgumentNullException.ThrowIfNull(request);
        ValidateActor(actorId);
        if (request.Reason is { Length: > MaxReasonLength })
        {
            return Refused(NhAssistantValidationErrors.Failed("reason", NhAssistantFieldErrors.TooLong));
        }

        if (request.AsPackageVersion)
        {
            return await PromoteAsPackageVersionAsync(variantId, request, actorId, cancellationToken);
        }

        var (refused, variant, live) = await ResolvePromotionAsync(variantId, request, cancellationToken);
        if (refused is not null)
        {
            return Refused(refused);
        }

        // The variant's data skills, checked against the live agent's skills in code before anything
        // changes.
        var configuration = services.GetRequiredService<INhAssistantAgentConfiguration>();
        var skills = request.MoveSkills
            ? (await configuration.GetAsync(variant!.Id, cancellationToken))?.DataSkills ?? []
            : [];
        var liveConfiguration = await configuration.GetAsync(live!.Id, cancellationToken);
        if (skills.Any(skill => liveConfiguration?.CodeSkills.Any(code => code.SkillId == skill.SkillId) == true))
        {
            return Refused(TaskResult.Failed(NhAssistantErrorCodes.SkillInCode, "The live agent declares a skill of the variant in code."));
        }

        // 1. The variant's definition becomes the live agent's next version, through the update path.
        var input = PromotionInput(variant!, live);
        if (input is null)
        {
            return Refused(NhAssistantValidationErrors.Failed(NhAssistantInstructionAssets.AssetField, NhAssistantFieldErrors.Invalid));
        }
        var updated = await administration.UpdateAsync(input, request.ExpectedVersion, actorId, cancellationToken);
        if (!updated.Success)
        {
            return Refused(updated);
        }

        // 2. Its skills move along when asked: each the next live version of the live agent's skill.
        var moved = new List<string>();
        foreach (var skill in skills.OrderBy(item => item.SkillId, StringComparer.Ordinal))
        {
            var current = liveConfiguration?.DataSkills.FirstOrDefault(item => item.SkillId == skill.SkillId);
            var saved = await configuration.SaveSkillAsync(live.Id, skill.SkillId, MoveRequest(skill, current, request.Reason), actorId, cancellationToken);
            if (!saved.Success)
            {
                return Refused(saved);
            }

            moved.Add(skill.SkillId);
        }

        // 3. The variant leaves: it never becomes live itself.
        var deleted = await administration.DeleteAsync(variant!.Id, actorId, cancellationToken);
        if (!deleted.Success)
        {
            return Refused(deleted);
        }

        await AuditPromotedAsync(live.Id, variant.Id, updated.Data!.Definition.Version, actorId, cancellationToken);
        var agent = await directory.GetAsync(live.Id, cancellationToken);
        if (agent is null)
        {
            return Refused(TaskResult.Failed(NhAssistantErrorCodes.AgentNotFound, "The agent was not found."));
        }

        return TaskResult<NhAssistantShadowVariantPromotionDto>.Succeeded(new NhAssistantShadowVariantPromotionDto(agent, variant.Id, moved));
    }

    /// <summary>
    /// The promotion of a package-linked variant as its package's next version of the live agent
    /// (FB11): the package service plans and writes it as an import, then one audit event records it.
    /// </summary>
    private async Task<TaskResult<NhAssistantShadowVariantPromotionDto>> PromoteAsPackageVersionAsync(
        string variantId,
        NhAssistantShadowVariantPromotionRequest request,
        string actorId,
        CancellationToken cancellationToken)
    {
        // The skills are parts of the package version; they move with it.
        if (request.MoveSkills)
        {
            return Refused(NhAssistantValidationErrors.Failed("moveSkills", NhAssistantFieldErrors.NotAllowed));
        }

        var (refused, variant, live) = await ResolvePromotionAsync(variantId, request, cancellationToken, asPackageVersion: true);
        if (refused is not null)
        {
            return Refused(refused);
        }

        var promoted = await services.GetRequiredService<NhAssistantAgentPackageService>()
            .PromoteVariantAsync(variant!, live!, actorId, request.Reason, cancellationToken);
        if (!promoted.Success)
        {
            return Refused(promoted);
        }

        var agent = await directory.GetAsync(live!.Id, cancellationToken);
        if (agent is null)
        {
            return Refused(TaskResult.Failed(NhAssistantErrorCodes.AgentNotFound, "The agent was not found."));
        }

        var package = promoted.Data!;
        await AuditPromotedAsync(live.Id, variant!.Id, agent.Version, actorId, cancellationToken, package);
        var skillPrefix = NhAssistantPackagePartKinds.Skill + ":";
        var skills = package.Parts
            .Where(part => part.AgentId == live.Id
                           && part.Part.StartsWith(skillPrefix, StringComparison.Ordinal)
                           && part.Outcome is NhAssistantPackagePartOutcomes.Created or NhAssistantPackagePartOutcomes.Changed or NhAssistantPackagePartOutcomes.Unchanged)
            .Select(part => part.Part[skillPrefix.Length..])
            .Order(StringComparer.Ordinal)
            .ToArray();
        return TaskResult<NhAssistantShadowVariantPromotionDto>.Succeeded(new NhAssistantShadowVariantPromotionDto(agent, variant.Id, skills)
        {
            Package = package
        });
    }

    /// <summary>
    /// The variant and its live agent, or why they cannot be promoted: the variant is a shadow variant
    /// created as data at its expected version, and its live agent may still have data variants
    /// (HD10), at its expected version, and the variant is not held by an agent package, unless the
    /// promotion takes it as the package's version.
    /// </summary>
    private async Task<(TaskResult? Refused, NhAssistantAgent? Variant, NhAssistantAgent? Live)> ResolvePromotionAsync(
        string variantId,
        NhAssistantShadowVariantPromotionRequest request,
        CancellationToken cancellationToken,
        bool asPackageVersion = false)
    {
        var variant = await catalog.FindAsync(variantId, includeDisabled: true, cancellationToken);
        if (variant is null)
        {
            return (TaskResult.Failed(NhAssistantErrorCodes.AgentNotFound, "The agent was not found."), null, null);
        }
        if (variant.Source != NhAssistantAgentSources.Admin || variant.ShadowOfAgentId is not { } liveAgentId)
        {
            return (TaskResult.Failed(NhAssistantErrorCodes.NotShadowVariant, "Only a shadow variant created as data is promoted onto its live agent."), null, null);
        }
        if (request.ExpectedVariantVersion is { } variantVersion && variantVersion != variant.Definition.Version)
        {
            return (TaskResult.Failed(NhAssistantAdminErrorCodes.VersionConflict, "The variant was changed by someone else."), null, null);
        }

        var live = await catalog.FindAsync(liveAgentId, includeDisabled: true, cancellationToken);
        if (live is null)
        {
            return (TaskResult.Failed(NhAssistantErrorCodes.AgentNotFound, "The live agent was not found."), null, null);
        }

        await configurations.EnsureCurrentAsync(cancellationToken);
        if (validator.CheckLiveAgentOfVariants(live.Id, registration.AgentRuns) is not null)
        {
            return (TaskResult.Failed(NhAssistantErrorCodes.ShadowVariantsNotAllowed, "The live agent may no longer have shadow variants created as data."), null, null);
        }
        if (request.ExpectedVersion != live.Definition.Version)
        {
            return (TaskResult.Failed(NhAssistantAdminErrorCodes.VersionConflict, "The agent was changed by someone else."), null, null);
        }

        // The variant is deleted last; an agent package that holds it keeps it (PK9), unless the
        // promotion makes it the package's version, which deletes it with its links.
        if (!asPackageVersion && await locks.CheckDeletionAsync(variant.Id, cancellationToken) is { } locked)
        {
            return (locked, null, null);
        }

        return (null, variant, live);
    }

    /// <summary>
    /// The update that copies the variant's definition onto the live agent, or <see langword="null"/>
    /// when the variant's instructions are an asset that is not available. The live agent keeps its
    /// display name and MCP assignments; an agent in code takes the fields it can override, with the
    /// asset's verified text as its instructions; an agent created at runtime also the variant's
    /// profile, classification, what it works on and its asset reference.
    /// </summary>
    private static NhAssistantAgentInput? PromotionInput(NhAssistantAgent variant, NhAssistantAgent live)
    {
        if (variant.InstructionsProblem is not null)
        {
            return null;
        }

        var definition = variant.Definition;
        var toRuntimeAgent = live.Source == NhAssistantAgentSources.Admin;
        var asset = toRuntimeAgent ? variant.InstructionsAsset : null;
        return new NhAssistantAgentInput(
            live.Id,
            live.Definition.DisplayNameKey,
            definition.DescriptionKey,
            asset is null ? definition.Instructions.Content : string.Empty,
            definition.ToolSelectors,
            [],
            definition.RequiredPolicy,
            definition.Autonomy,
            OfferedInChat: null,
            WorksOn: toRuntimeAgent ? variant.WorksOn : null,
            StartsOff: false,
            ProfileName: toRuntimeAgent ? definition.ProfileName : null,
            DataClassification: toRuntimeAgent && variant.DataClassification is { } classification
                ? NhAssistantDataClassificationNames.NameOf(classification)
                : null,
            Metadata: null,
            InstructionsAsset: asset)
        {
            KeepMcpServers = true
        };
    }

    /// <summary>
    /// The live version of a variant's data skill as the next live version of the live agent's skill
    /// with the same id, or as a new skill when it has none.
    /// </summary>
    private static NhAssistantDataSkillRequest MoveRequest(
        NhAssistantDataSkillDto skill,
        NhAssistantDataSkillDto? current,
        string? reason)
    {
        return new NhAssistantDataSkillRequest(
            skill.Description,
            skill.InstructionsAsset is null ? skill.Instructions : null,
            skill.Tools,
            skill.Profile,
            skill.RuleHandler,
            skill.MaxTokensPerRun,
            skill.DailyToolCalls,
            skill.DailyTokens,
            skill.ApprovalPolicy,
            current?.Version,
            reason,
            current is null ? null : NhAssistantSkillApplyModes.Live,
            StartsOff: false,
            skill.DataClassification,
            skill.AcceptsFiles,
            skill.MaxFileBytes,
            skill.MaxFilePages,
            skill.MaxFilesPerTurn,
            skill.MaxFileTokensPerTurn)
        {
            InstructionsAsset = skill.InstructionsAsset
        };
    }

    private async Task AuditPromotedAsync(
        string liveAgentId,
        string variantId,
        int version,
        string actorId,
        CancellationToken cancellationToken,
        NhAssistantAgentPackageImportDto? package = null)
    {
        var evt = new NhAssistantAuditEvent(
            NhAssistantAuditEventKind.AdminAgentShadowVariantPromoted,
            Guid.Empty,
            Guid.Empty,
            actorId,
            liveAgentId,
            version,
            DateTimeOffset.UtcNow)
        {
            ObjectId = variantId,
            LiveAgentId = liveAgentId,
            ResultCode = package is null ? NhAssistantShadowVariantPromotions.Promoted : NhAssistantShadowVariantPromotions.PromotedAsPackageVersion,
            PackageId = package?.PackageId,
            PackageVersion = package?.PackageVersion,
            PackageHash = package?.Hash
        };
        foreach (var sink in _auditSinks)
        {
            await sink.RecordAsync(evt, cancellationToken);
        }
    }

    private async Task<TaskResult<NhAssistantAgentInEffectDto>> InEffectAsync(
        TaskResult<NhAssistantAgent> changed,
        CancellationToken cancellationToken)
    {
        if (!changed.Success)
        {
            return TaskResult<NhAssistantAgentInEffectDto>.Failed(changed);
        }

        // Another administrator may delete the agent right after the change.
        var agent = await directory.GetAsync(changed.Data!.Id, cancellationToken);
        return agent is null
            ? TaskResult<NhAssistantAgentInEffectDto>.Failed(NhAssistantErrorCodes.AgentNotFound, "The agent was not found.")
            : TaskResult<NhAssistantAgentInEffectDto>.Succeeded(agent);
    }

    private static TaskResult<NhAssistantShadowVariantPromotionDto> Refused(TaskResult result)
    {
        return TaskResult<NhAssistantShadowVariantPromotionDto>.Failed(result);
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
/// The result codes of <c>AdminAgentShadowVariantPromoted</c>.
/// </summary>
public static class NhAssistantShadowVariantPromotions
{
    /// <summary>The variant's definition went onto its live agent through the update path.</summary>
    public const string Promoted = "promoted";

    /// <summary>
    /// The variant's parts became its package's next version of the live agent
    /// (<see cref="NhAssistantShadowVariantPromotionRequest.AsPackageVersion"/>).
    /// </summary>
    public const string PromotedAsPackageVersion = "promoted-as-package-version";
}
