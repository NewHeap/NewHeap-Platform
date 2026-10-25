using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.Common.Models;

namespace NewHeap.Platform.AI.Chat.AspNet.AgentRuns;

/// <summary>
/// Which data skills and agents created in the administration take their instructions from one of the
/// application's text assets (<c>UseTextAssetSource</c>), and which version and hash of it each one
/// names. It only reads: a new version of the asset never takes effect by itself, so an application that
/// publishes one finds here what an administrator saves as the next version of each skill or agent.
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

/// <inheritdoc cref="INhAssistantInstructionAssetUsage"/>
internal sealed class NhAssistantInstructionAssetUsageService(
    NhAssistantRunConfigurationStore runConfiguration,
    NhAssistantAdminStore admin) : INhAssistantInstructionAssetUsage
{
    public async Task<TaskResult<NhAssistantInstructionAssetUsageDto>> ListAsync(
        string assetId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(assetId);
        // Only a well-formed id can be named, so a provider whose comparison ignores case finds nothing
        // another provider would not.
        if (!NhAssistantNames.IsSegment(assetId) || assetId.Length > NhAssistantInstructionAssetReference.MaxAssetIdLength)
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
        var agents = (await admin.ListAgentsNamingAssetAsync(assetId, cancellationToken))
            .Select(agent => new NhAssistantInstructionAssetAgentUseDto(
                agent.Id,
                agent.Version,
                agent.InstructionsAssetVersion,
                agent.InstructionsHash))
            .ToArray();
        return TaskResult<NhAssistantInstructionAssetUsageDto>.Succeeded(new NhAssistantInstructionAssetUsageDto(assetId, skills, agents));
    }
}
