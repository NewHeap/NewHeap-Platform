using NewHeap.Platform.AI.Chat;
using NewHeap.Platform.AI.Chat.AspNet.AgentRuns;
using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.Common.Models;
using SampleProjectManagement.Api.Composition;

namespace SampleProjectManagement.Api.Services;

/// <summary>
/// SPM-295: the application adds a skill and its trigger to the project planner at runtime, as an
/// import of a skill package would, through <see cref="INhAssistantAgentConfiguration"/>. The
/// progress note is data: the skill names the progress-note rules and the trigger names the
/// completed-task event, both registered in code by the planner's composition. Both start in shadow,
/// so the team reads the notes in the planner's shadow threads first; the operator then puts them
/// live without a deploy. Removing them takes the note out of effect and keeps its versions.
/// </summary>
public sealed class SampleAgentConfigurationService(
    INhAssistantAgentConfiguration configuration,
    INhAssistantAgentControl agents)
{
    public const string ProgressNoteSkill = "progress-note";
    public const string ProgressNoteTriggerId = "planner-progress-note";

    private const string AgentId = SampleProjectPlannerComposition.AgentId;

    /// <summary>
    /// Adds the progress note to the planner: a skill that works by the progress-note rules and a
    /// trigger that starts it on every completed task. Both start in shadow.
    /// </summary>
    public async Task<TaskResult> AddProgressNoteAsync(Guid operatorId, CancellationToken cancellationToken)
    {
        var actorId = operatorId.ToString("D");
        var skill = await configuration.SaveSkillAsync(
            AgentId,
            ProgressNoteSkill,
            new NhAssistantDataSkillRequest(
                "Notes how many of the project's tasks are completed after a task was completed.",
                RuleHandler: SampleProjectPlannerComposition.ProgressNoteRules,
                Reason: "Imported from the progress package"),
            actorId,
            cancellationToken);
        if (!skill.Success)
        {
            return skill;
        }

        return await configuration.SaveTriggerAsync(
            AgentId,
            ProgressNoteTriggerId,
            new NhAssistantDataTriggerRequest(
                NhAssistantDataTriggerSources.Event,
                ProgressNoteSkill,
                Event: SampleProjectPlannerComposition.TaskCompletedEvent,
                // The planner's own follow-up starts on the same event; the note waits for it.
                Conflict: NhAssistantTriggerConflictNames.Queue,
                DebounceSeconds: 0,
                Reason: "Imported from the progress package"),
            actorId,
            cancellationToken);
    }

    /// <summary>
    /// Puts the progress note live: its trigger and its skill both leave shadow.
    /// </summary>
    public async Task<TaskResult> PutProgressNoteLiveAsync(Guid operatorId, string? reason, CancellationToken cancellationToken)
    {
        var actorId = operatorId.ToString("D");
        var trigger = await configuration.SetTriggerShadowAsync(AgentId, ProgressNoteTriggerId, false, actorId, reason, cancellationToken);
        if (!trigger.Success)
        {
            return trigger;
        }

        return await agents.SetSkillShadowAsync(AgentId, ProgressNoteSkill, false, actorId, reason, cancellationToken);
    }

    /// <summary>
    /// Takes the progress note out of effect: first its trigger, then the skill it names.
    /// </summary>
    public async Task<TaskResult> RemoveProgressNoteAsync(Guid operatorId, CancellationToken cancellationToken)
    {
        var actorId = operatorId.ToString("D");
        var trigger = await configuration.RemoveTriggerAsync(AgentId, ProgressNoteTriggerId, null, actorId, cancellationToken: cancellationToken);
        if (!trigger.Success && !HasCode(trigger, NhAssistantErrorCodes.TriggerNotFound))
        {
            return trigger;
        }

        var skill = await configuration.RemoveSkillAsync(AgentId, ProgressNoteSkill, null, actorId, cancellationToken: cancellationToken);
        return skill.Success || HasCode(skill, NhAssistantErrorCodes.SkillNotFound) ? TaskResult.Succeeded() : skill;
    }

    private static bool HasCode(TaskResult result, string code)
    {
        return result.GetResultItems().Any(item => item.Name == code);
    }
}
