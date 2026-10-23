using NewHeap.Platform.AI;
using NewHeap.Platform.AI.Chat;
using NewHeap.Platform.AI.Chat.AspNet.AgentRuns;
using NewHeap.Platform.Common.Models;
using SampleProjectManagement.Api.Composition;
using SampleProjectManagement.Core.Services;

namespace SampleProjectManagement.Api.Services;

/// <summary>
/// SPM-320: the planner's risk review is a skill defined as data whose instructions are the sample's
/// own text asset instead of text an administrator writes. The skill stores the asset's id, version
/// and hash; the assistant reads the text from <see cref="SampleInstructionAssets"/> and checks the
/// hash at every load. Moving to version 2 of the asset is the skill's next version, saved
/// explicitly: the assistant never follows a newer version on its own.
/// </summary>
public sealed class SampleInstructionAssetService(INhAssistantAgentConfiguration configuration)
{
    public const string RiskReviewSkill = "risk-review";

    private const string AgentId = SampleProjectPlannerComposition.AgentId;
    private const string Description = "Reviews which tasks put an active project's deadline at risk.";

    /// <summary>
    /// Adds the risk review to the planner with version 1 of its asset; it starts in shadow, as every
    /// new skill does.
    /// </summary>
    public Task<TaskResult<NhAssistantDataSkillDto>> AddRiskReviewAsync(Guid operatorId, CancellationToken cancellationToken)
    {
        return configuration.SaveSkillAsync(
            AgentId,
            RiskReviewSkill,
            new NhAssistantDataSkillRequest(
                Description,
                Tools: [ProjectAiTools.ChangeStatusToolId],
                Reason: "Risk review from the application's instructions")
            {
                InstructionsAsset = SampleInstructionAssets.ReferenceOf(SampleInstructionAssets.RiskReviewV1)
            },
            operatorId.ToString("D"),
            cancellationToken);
    }

    /// <summary>
    /// Saves the risk review's next version with <paramref name="asset"/>, as an administrator does
    /// after reading the new version of the text; it takes effect at once in the skill's mode.
    /// </summary>
    public async Task<TaskResult<NhAssistantDataSkillDto>> UseRiskReviewVersionAsync(
        NhAiTextAsset asset,
        Guid operatorId,
        CancellationToken cancellationToken)
    {
        var live = (await configuration.GetAsync(AgentId, cancellationToken))?.DataSkills
            .FirstOrDefault(skill => skill.SkillId == RiskReviewSkill);
        if (live is null)
        {
            return TaskResult<NhAssistantDataSkillDto>.Failed(NhAssistantErrorCodes.SkillNotFound, "The planner has no risk review.");
        }

        return await configuration.SaveSkillAsync(
            AgentId,
            RiskReviewSkill,
            new NhAssistantDataSkillRequest(
                Description,
                Tools: [ProjectAiTools.ChangeStatusToolId],
                ExpectedVersion: live.Version,
                Reason: $"Version {asset.Manifest.Version} of the risk review's instructions",
                Apply: NhAssistantSkillApplyModes.Live)
            {
                InstructionsAsset = SampleInstructionAssets.ReferenceOf(asset)
            },
            operatorId.ToString("D"),
            cancellationToken);
    }

    /// <summary>
    /// Removes the risk review; its versions stay as history.
    /// </summary>
    public async Task<TaskResult> RemoveRiskReviewAsync(Guid operatorId, CancellationToken cancellationToken)
    {
        var removed = await configuration.RemoveSkillAsync(AgentId, RiskReviewSkill, null, operatorId.ToString("D"), cancellationToken: cancellationToken);
        return removed.Success || removed.GetResultItems().Any(item => item.Name == NhAssistantErrorCodes.SkillNotFound)
            ? TaskResult.Succeeded()
            : removed;
    }
}
