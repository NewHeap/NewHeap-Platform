using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.Common.Models;

namespace NewHeap.Platform.AI.Chat.AspNet.AgentRuns;

/// <summary>
/// Which data skills and agents created in the administration take their instructions from one of the
/// application's text assets (<c>UseTextAssetSource</c>), and which version and hash of it each one
/// names. A new version of the asset never takes effect by itself: an application that publishes one
/// finds here what an administrator saves as the next version of each skill or agent, and can propose
/// the new version to every live data skill that names an older one as a candidate in shadow (FB32),
/// which an administrator compares and promotes or discards.
/// </summary>
public interface INhAssistantInstructionAssetUsage
{
    /// <summary>
    /// Every version of every data skill, whatever its status, and every agent created in the
    /// administration that names the asset <paramref name="assetId"/>, with the asset version and hash it
    /// names: skills by agent and skill id with the newest version first, agents by id, ordinal. An asset
    /// nothing names has empty lists. An id that is not an asset id (lowercase dash-case, at most 128
    /// characters) fails with the field error <c>assetId: invalid</c>. Nothing is audited; no text is read.
    /// </summary>
    Task<TaskResult<NhAssistantInstructionAssetUsageDto>> ListAsync(string assetId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Proposes version <paramref name="assetVersion"/> of the asset (FB32) to every live data skill that
    /// names an older version of it: each gets a candidate in shadow through the data skill's own save
    /// (<see cref="INhAssistantAgentConfiguration.SaveSkillAsync"/> with apply <c>shadow</c> on its live
    /// version), the live version's fields with the new asset version and its hash, and
    /// <paramref name="reason"/> (by default <c>asset {id}@{version}</c>). The skill keeps the newest
    /// candidate (FB62): a candidate of an older version of the asset, or one that names no asset or
    /// another one, is discarded, as every new version discards one, while a candidate of a newer version
    /// stays and nothing is saved. The store decides in the save's transaction, so of two proposals at once
    /// the newer version stays. Where the application replaced <see cref="INhAssistantAgentConfiguration"/>,
    /// for example with a decorator that adds its own checks, each candidate is saved through that
    /// replacement instead, as any save is: the check before the save still answers <c>newer-proposed</c>,
    /// but two proposals at once are no longer decided by the store. The candidate runs next to the live
    /// version until an administrator promotes or discards it. The outcome is reported per skill
    /// (<see cref="NhAssistantInstructionAssetCandidateOutcomes"/>): <c>proposed</c>,
    /// <c>already-proposed</c> when its candidate already names the version and hash,
    /// <c>newer-proposed</c> when its candidate names a newer version, <c>locked</c> when an agent
    /// package locks the skill's instructions (<c>assistant-package-locked</c> with the save's field
    /// errors) and <c>refused</c> with the save's code and field errors; one skill never stops the
    /// others. Agents created in the administration that name an older version are listed only: an agent
    /// has no candidate, so an administrator saves it as its next version. Fails, saving nothing, with
    /// the field errors <c>assetId: invalid</c>, <c>assetVersion: invalid</c> (below 1) and
    /// <c>reason: too-long</c> (above 500 characters), <c>assistant-instructions-asset-not-found</c>
    /// without a text asset source or without that asset version,
    /// <c>assistant-instructions-asset-unavailable</c> when the source fails and
    /// <c>instructionsAsset: invalid</c> for an asset that cannot be instructions. Each candidate is
    /// audited as its save (<c>AdminAgentSkillDefinitionChanged</c>, <c>candidate</c>).
    /// </summary>
    Task<TaskResult<NhAssistantInstructionAssetCandidatesDto>> ProposeCandidatesAsync(
        string assetId,
        int assetVersion,
        string actorId,
        string? reason = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// The data skills and agents that name one text asset of the application.
/// </summary>
/// <param name="AssetId">The asset.</param>
/// <param name="Skills">The versions of data skills that name it, live, candidate or kept as history.</param>
/// <param name="Agents">The agents created in the administration whose instructions it is.</param>
public sealed record NhAssistantInstructionAssetUsageDto(
    string AssetId,
    IReadOnlyList<NhAssistantInstructionAssetSkillUseDto> Skills,
    IReadOnlyList<NhAssistantInstructionAssetAgentUseDto> Agents);

/// <summary>
/// One version of a data skill whose instructions name the asset.
/// </summary>
/// <param name="AgentId">The skill's agent.</param>
/// <param name="SkillId">The skill.</param>
/// <param name="Version">The skill's version.</param>
/// <param name="Status">One of <see cref="NhAssistantSkillDefinitionStatuses"/>: <c>live</c> and
/// <c>candidate</c> are in effect or on trial, <c>superseded</c> and <c>discarded</c> are history.</param>
/// <param name="AssetVersion">The version of the asset it names.</param>
/// <param name="Hash">The SHA-256 of the asset's content it names, lowercase hex.</param>
public sealed record NhAssistantInstructionAssetSkillUseDto(
    string AgentId,
    string SkillId,
    int Version,
    string Status,
    int AssetVersion,
    string Hash);

/// <summary>
/// An agent created in the administration whose instructions are the asset.
/// </summary>
/// <param name="AgentId">The agent.</param>
/// <param name="Version">The version of the agent's definition.</param>
/// <param name="AssetVersion">The version of the asset it names.</param>
/// <param name="Hash">The SHA-256 of the asset's content it names, lowercase hex.</param>
public sealed record NhAssistantInstructionAssetAgentUseDto(
    string AgentId,
    int Version,
    int AssetVersion,
    string Hash);

/// <summary>
/// What <see cref="INhAssistantInstructionAssetUsage.ProposeCandidatesAsync"/> did with a new version of
/// an asset (FB32).
/// </summary>
/// <param name="AssetId">The asset.</param>
/// <param name="AssetVersion">The version that was proposed.</param>
/// <param name="Hash">The SHA-256 of that version's content, lowercase hex, which every candidate names.</param>
/// <param name="Skills">Every live data skill that named an older version, by agent and skill id
/// (ordinal), with its outcome.</param>
/// <param name="Agents">The agents created in the administration that name an older version, by id;
/// they stay as they are until an administrator saves each.</param>
public sealed record NhAssistantInstructionAssetCandidatesDto(
    string AssetId,
    int AssetVersion,
    string Hash,
    IReadOnlyList<NhAssistantInstructionAssetCandidateDto> Skills,
    IReadOnlyList<NhAssistantInstructionAssetAgentUseDto> Agents);

/// <summary>
/// The outcome of the proposal of a new asset version to one live data skill.
/// </summary>
/// <param name="AgentId">The skill's agent.</param>
/// <param name="SkillId">The skill.</param>
/// <param name="LiveVersion">The skill's live version, on which the candidate was saved.</param>
/// <param name="LiveAssetVersion">The older version of the asset the live version names.</param>
/// <param name="Outcome">One of <see cref="NhAssistantInstructionAssetCandidateOutcomes"/>.</param>
/// <param name="CandidateVersion">For <c>proposed</c> and <c>already-proposed</c>: the skill's candidate
/// version that names the new asset version; for <c>newer-proposed</c>: the skill's candidate version that
/// names a newer one; otherwise <see langword="null"/>.</param>
/// <param name="Code">For <c>locked</c> and <c>refused</c>: the failure code, such as
/// <c>assistant-package-locked</c> or <c>assistant-version-conflict</c>; otherwise <see langword="null"/>.</param>
/// <param name="Errors">For <c>locked</c> and <c>refused</c>: the save's field errors by field, such as
/// <c>instructions: locked</c>; otherwise <see langword="null"/>.</param>
public sealed record NhAssistantInstructionAssetCandidateDto(
    string AgentId,
    string SkillId,
    int LiveVersion,
    int LiveAssetVersion,
    string Outcome,
    int? CandidateVersion,
    string? Code,
    IReadOnlyDictionary<string, string[]>? Errors);

/// <summary>
/// The outcomes of <see cref="NhAssistantInstructionAssetCandidateDto.Outcome"/>.
/// </summary>
public static class NhAssistantInstructionAssetCandidateOutcomes
{
    /// <summary>The skill's candidate names the new asset version now; it runs in shadow next to the live version.</summary>
    public const string Proposed = "proposed";

    /// <summary>The skill's candidate already named the new asset version with its hash; nothing was saved.</summary>
    public const string AlreadyProposed = "already-proposed";

    /// <summary>
    /// The skill's candidate names a newer version of the asset than the one proposed, and stays; nothing
    /// was saved. An older version that is proposed late, for example by a delayed job, never replaces a
    /// newer candidate.
    /// </summary>
    public const string NewerProposed = "newer-proposed";

    /// <summary>
    /// An agent package the skill was imported from locks its instructions; nothing was saved, until an
    /// administrator unlinks the skill or the package brings the new version.
    /// </summary>
    public const string Locked = "locked";

    /// <summary>The skill's save refused the candidate; nothing was saved for the skill.</summary>
    public const string Refused = "refused";
}

/// <inheritdoc cref="INhAssistantInstructionAssetUsage"/>
internal sealed class NhAssistantInstructionAssetUsageService(
    NhAssistantRunConfigurationStore runConfiguration,
    NhAssistantAdminStore admin,
    NhAssistantInstructionAssets instructionAssets,
    INhAssistantAgentConfiguration configuration) : INhAssistantInstructionAssetUsage
{
    private const int MaxActorIdLength = 256;
    private const int MaxReasonLength = 500;

    public async Task<TaskResult<NhAssistantInstructionAssetUsageDto>> ListAsync(
        string assetId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(assetId);
        if (!IsAssetId(assetId))
        {
            return TaskResult<NhAssistantInstructionAssetUsageDto>.Failed(NhAssistantValidationErrors.Failed("assetId", NhAssistantFieldErrors.Invalid));
        }

        var skills = (await runConfiguration.ListSkillsNamingAssetAsync(assetId, cancellationToken))
            .Select(skill => new NhAssistantInstructionAssetSkillUseDto(
                skill.AgentId,
                skill.SkillId,
                skill.Version,
                skill.Status,
                skill.InstructionsAssetVersion!.Value,
                skill.InstructionsHash ?? string.Empty))
            .ToArray();
        var agents = await ListAgentsAsync(assetId, cancellationToken);
        return TaskResult<NhAssistantInstructionAssetUsageDto>.Succeeded(new NhAssistantInstructionAssetUsageDto(assetId, skills, agents));
    }

    public async Task<TaskResult<NhAssistantInstructionAssetCandidatesDto>> ProposeCandidatesAsync(
        string assetId,
        int assetVersion,
        string actorId,
        string? reason = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(assetId);
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        if (actorId.Length > MaxActorIdLength)
        {
            throw new ArgumentOutOfRangeException(nameof(actorId), $"An actor id has at most {MaxActorIdLength} characters.");
        }

        reason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        var errors = new NhAssistantValidationErrors()
            .Require(IsAssetId(assetId), "assetId", NhAssistantFieldErrors.Invalid)
            .Require(assetVersion >= 1, "assetVersion", NhAssistantFieldErrors.Invalid)
            .Require(reason is not { Length: > MaxReasonLength }, "reason", NhAssistantFieldErrors.TooLong);
        if (!errors.IsEmpty)
        {
            return TaskResult<NhAssistantInstructionAssetCandidatesDto>.Failed(errors.ToResult());
        }

        // The application's source gives the hash every candidate names, checked as a save checks it.
        var resolved = await instructionAssets.CheckVersionAsync(
            assetId,
            assetVersion,
            NhAssistantAgentConfigurationLimits.MaxSkillInstructionsLength,
            cancellationToken);
        if (AssetFailure(resolved) is { } unusable)
        {
            return TaskResult<NhAssistantInstructionAssetCandidatesDto>.Failed(unusable);
        }

        var reference = new NhAssistantInstructionAssetReference(assetId, assetVersion, resolved.Asset!.Manifest.ContentHash);
        var versions = await runConfiguration.ListSkillsNamingAssetAsync(assetId, cancellationToken);
        var skills = new List<NhAssistantInstructionAssetCandidateDto>();
        foreach (var live in versions.Where(item => item.Status == NhAssistantSkillDefinitionStatuses.Live
                                                    && item.InstructionsAssetVersion < assetVersion))
        {
            skills.Add(await ProposeAsync(live, versions, reference, actorId, reason ?? "asset " + reference.InstructionsVersion, cancellationToken));
        }

        var agents = (await ListAgentsAsync(assetId, cancellationToken))
            .Where(agent => agent.AssetVersion < assetVersion)
            .ToArray();
        return TaskResult<NhAssistantInstructionAssetCandidatesDto>.Succeeded(new NhAssistantInstructionAssetCandidatesDto(
            assetId,
            assetVersion,
            reference.Hash,
            skills,
            agents));
    }

    /// <summary>
    /// Saves the candidate of one live data skill: its live version's fields with the new asset version,
    /// through the skill's own save, unless its candidate already names that version and hash or a newer
    /// version. The candidates read before are checked first, so a repeat runs no save. The built-in
    /// configuration then saves through the store's compare-and-set, which checks again in the save's
    /// transaction and decides when another proposal saved in the meantime. An application's replacement
    /// of <see cref="INhAssistantAgentConfiguration"/> is never bypassed: it receives the save as any save,
    /// and only the check before it keeps a newer candidate then.
    /// </summary>
    private async Task<NhAssistantInstructionAssetCandidateDto> ProposeAsync(
        AssistantAgentSkillDefinition live,
        IReadOnlyList<AssistantAgentSkillDefinition> versions,
        NhAssistantInstructionAssetReference reference,
        string actorId,
        string reason,
        CancellationToken cancellationToken)
    {
        var liveAssetVersion = live.InstructionsAssetVersion!.Value;
        var candidate = versions.FirstOrDefault(item => item.AgentId == live.AgentId
                                                        && item.SkillId == live.SkillId
                                                        && item.Status == NhAssistantSkillDefinitionStatuses.Candidate);
        if (candidate is not null && KeptOutcome(candidate, reference) is { } outcome)
        {
            return Kept(live, candidate, outcome);
        }

        var part = NhAssistantAgentPackageParts.SkillOf(live) with { InstructionsAsset = reference };
        var request = NhAssistantAgentPackageParts.RequestOf(part, live.Version, asCandidate: true, reason);
        // A save in shadow answers the candidate it stored.
        TaskResult<NhAssistantDataSkillDto> saved;
        if (configuration is NhAssistantAgentConfiguration builtIn)
        {
            var (stored, kept) = await builtIn.SaveAssetCandidateAsync(live.AgentId, live.SkillId, request, actorId, cancellationToken);
            if (kept is not null)
            {
                return Kept(live, kept, KeptOutcome(kept, reference) ?? NhAssistantInstructionAssetCandidateOutcomes.NewerProposed);
            }

            saved = stored!;
        }
        else
        {
            saved = await configuration.SaveSkillAsync(live.AgentId, live.SkillId, request, actorId, cancellationToken);
        }

        if (saved.Success)
        {
            return new NhAssistantInstructionAssetCandidateDto(
                live.AgentId,
                live.SkillId,
                live.Version,
                liveAssetVersion,
                NhAssistantInstructionAssetCandidateOutcomes.Proposed,
                saved.Data!.Version,
                null,
                null);
        }

        // A package's lock of the instructions refuses the save with the field error {field}: locked.
        var fields = NhAssistantResults.FieldErrorsOf(saved);
        var locked = fields.Values.Any(codes => codes.Contains(NhAssistantFieldErrors.Locked, StringComparer.Ordinal));
        return new NhAssistantInstructionAssetCandidateDto(
            live.AgentId,
            live.SkillId,
            live.Version,
            liveAssetVersion,
            locked ? NhAssistantInstructionAssetCandidateOutcomes.Locked : NhAssistantInstructionAssetCandidateOutcomes.Refused,
            null,
            locked ? NhAssistantErrorCodes.PackageLocked : NhAssistantResults.CodeOf(saved),
            fields.Count == 0 ? null : fields.ToDictionary(item => item.Key, item => item.Value.ToArray(), StringComparer.Ordinal));
    }

    /// <summary>
    /// Why the skill's <paramref name="candidate"/> stays: <c>newer-proposed</c> when it names a newer
    /// version of the asset, <c>already-proposed</c> when it names the proposed version and hash;
    /// <see langword="null"/> when the proposal replaces it.
    /// </summary>
    private static string? KeptOutcome(AssistantAgentSkillDefinition candidate, NhAssistantInstructionAssetReference reference)
    {
        if (!string.Equals(candidate.InstructionsAssetId, reference.AssetId, StringComparison.Ordinal))
        {
            return null;
        }
        if (candidate.InstructionsAssetVersion > reference.Version)
        {
            return NhAssistantInstructionAssetCandidateOutcomes.NewerProposed;
        }

        return candidate.InstructionsAssetVersion == reference.Version
               && string.Equals(candidate.InstructionsHash, reference.Hash, StringComparison.Ordinal)
            ? NhAssistantInstructionAssetCandidateOutcomes.AlreadyProposed
            : null;
    }

    private static NhAssistantInstructionAssetCandidateDto Kept(
        AssistantAgentSkillDefinition live,
        AssistantAgentSkillDefinition candidate,
        string outcome)
    {
        return new NhAssistantInstructionAssetCandidateDto(
            live.AgentId,
            live.SkillId,
            live.Version,
            live.InstructionsAssetVersion!.Value,
            outcome,
            candidate.Version,
            null,
            null);
    }

    /// <summary>
    /// The agents created in the administration whose instructions name the asset, by id.
    /// </summary>
    private async Task<NhAssistantInstructionAssetAgentUseDto[]> ListAgentsAsync(string assetId, CancellationToken cancellationToken)
    {
        return (await admin.ListAgentsNamingAssetAsync(assetId, cancellationToken))
            .Select(agent => new NhAssistantInstructionAssetAgentUseDto(
                agent.Id,
                agent.Version,
                agent.InstructionsAssetVersion,
                agent.InstructionsHash))
            .ToArray();
    }

    /// <summary>
    /// Why the asset version cannot be proposed, as a save answers it; <see langword="null"/> for a
    /// verified asset.
    /// </summary>
    private static TaskResult? AssetFailure(NhAssistantResolvedInstructions resolved)
    {
        return resolved.Problem switch
        {
            null when resolved.Asset is not null => null,
            NhAssistantRunConfigurationProblems.AssetUnavailable => TaskResult.Failed(
                NhAssistantAdminErrorCodes.InstructionsAssetUnavailable,
                "The application's text asset source failed."),
            NhAssistantRunConfigurationProblems.AssetInvalid => NhAssistantValidationErrors.Failed(
                NhAssistantInstructionAssets.AssetField,
                NhAssistantFieldErrors.Invalid),
            _ => TaskResult.Failed(
                NhAssistantAdminErrorCodes.InstructionsAssetNotFound,
                "The application has no text asset with this id and version.")
        };
    }

    /// <summary>
    /// Only a well-formed id can be named, so a provider whose comparison ignores case finds nothing
    /// another provider would not.
    /// </summary>
    private static bool IsAssetId(string assetId)
    {
        return NhAssistantNames.IsSegment(assetId) && assetId.Length <= NhAssistantInstructionAssetReference.MaxAssetIdLength;
    }
}
