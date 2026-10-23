using NewHeap.Platform.AI.Chat;
using NewHeap.Platform.AI.Chat.AspNet.AgentRuns;
using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.Common.Models;
using SampleProjectManagement.Api.Composition;
using SampleProjectManagement.Core.Services;

namespace SampleProjectManagement.Api.Services;

/// <summary>
/// SPM-308: a trigger the application adds to the planner on every status change of a project, which
/// selects the event's <c>not-own-change</c> filter: the planner's own status change of a project does
/// not start its task follow-up on that project again, while a person's change does.
/// SPM-297: a change of the progress note is evaluated before it goes live: saved for shadow, it is
/// the note's candidate, which notes progress in the planner's shadow threads next to the live note
/// on the same completed tasks. The comparison counts both; promoting makes the candidate the live
/// note.
/// SPM-296: the project starter is an agent an administrator created in the administration. The
/// application gives it run settings as data: it answers to a technical account, works by the
/// kickoff rules and starts on every created project. It has no grants: it proposes with the rights
/// of that technical account, which the platform rebuilds for every decision.
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
    public const string StarterTriggerId = "project-starter-created";
    public const string StatusFollowUpTriggerId = "planner-status-follow-up";

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
    /// SPM-308: adds a trigger that starts the planner's task follow-up when a project's status
    /// changed, except for a change the planner made itself on that project. It starts in shadow.
    /// </summary>
    public Task<TaskResult<NhAssistantDataTriggerDto>> AddStatusFollowUpAsync(Guid operatorId, CancellationToken cancellationToken)
    {
        return configuration.SaveTriggerAsync(
            AgentId,
            StatusFollowUpTriggerId,
            new NhAssistantDataTriggerRequest(
                NhAssistantDataTriggerSources.Event,
                SampleProjectPlannerComposition.TaskFollowUpSkill,
                Event: SampleProjectPlannerComposition.StatusChangedEvent,
                Conflict: NhAssistantTriggerConflictNames.Queue,
                DebounceSeconds: 0,
                Reason: "Follow up a project whose status changed",
                Filters: [SampleProjectPlannerComposition.NotOwnChangeFilter]),
            operatorId.ToString("D"),
            cancellationToken);
    }

    /// <summary>
    /// SPM-308: removes the status follow-up.
    /// </summary>
    public async Task<TaskResult> RemoveStatusFollowUpAsync(Guid operatorId, CancellationToken cancellationToken)
    {
        var trigger = await configuration.RemoveTriggerAsync(AgentId, StatusFollowUpTriggerId, null, operatorId.ToString("D"), cancellationToken: cancellationToken);
        return trigger.Success || HasCode(trigger, NhAssistantErrorCodes.TriggerNotFound) ? TaskResult.Succeeded() : trigger;
    }

    /// <summary>
    /// SPM-297: saves the progress note that notes the share of completed tasks as the candidate of
    /// the live note.
    /// </summary>
    public async Task<TaskResult<NhAssistantDataSkillDto>> EvaluateProgressPercentageAsync(Guid operatorId, CancellationToken cancellationToken)
    {
        var live = await LiveProgressNoteAsync(cancellationToken);
        if (live is null)
        {
            return TaskResult<NhAssistantDataSkillDto>.Failed(NhAssistantErrorCodes.SkillNotFound, "The planner has no progress note.");
        }

        return await configuration.SaveSkillAsync(
            AgentId,
            ProgressNoteSkill,
            new NhAssistantDataSkillRequest(
                "Notes which share of the project's tasks is completed after a task was completed.",
                RuleHandler: SampleProjectPlannerComposition.ProgressPercentageRules,
                ExpectedVersion: live.Version,
                Reason: "Notes progress as a percentage",
                Apply: NhAssistantSkillApplyModes.Shadow),
            operatorId.ToString("D"),
            cancellationToken);
    }

    /// <summary>
    /// SPM-297: the comparison of the progress note's candidate with the live note.
    /// </summary>
    public Task<TaskResult<NhAssistantSkillComparisonDto>> CompareProgressNoteAsync(CancellationToken cancellationToken)
    {
        return configuration.CompareSkillCandidateAsync(AgentId, ProgressNoteSkill, cancellationToken: cancellationToken);
    }

    /// <summary>
    /// SPM-297: makes the progress note's candidate the live note.
    /// </summary>
    public async Task<TaskResult<NhAssistantDataSkillDto>> PromoteProgressNoteAsync(Guid operatorId, string? reason, CancellationToken cancellationToken)
    {
        if ((await LiveProgressNoteAsync(cancellationToken))?.Candidate is not { } candidate)
        {
            return TaskResult<NhAssistantDataSkillDto>.Failed(NhAssistantErrorCodes.SkillCandidateNotFound, "The progress note has no candidate.");
        }

        return await configuration.PromoteSkillCandidateAsync(AgentId, ProgressNoteSkill, candidate.Version, operatorId.ToString("D"), reason, cancellationToken);
    }

    private async Task<NhAssistantDataSkillDto?> LiveProgressNoteAsync(CancellationToken cancellationToken)
    {
        return (await configuration.GetAsync(AgentId, cancellationToken))?.DataSkills.FirstOrDefault(skill => skill.SkillId == ProgressNoteSkill);
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

    /// <summary>
    /// SPM-296: lets an agent created in the administration start projects: it answers to
    /// <paramref name="accountId"/>, works by the kickoff rules under the intake approval policy and
    /// starts on every created project. Its trigger starts in shadow; <paramref name="live"/> puts it
    /// live at once.
    /// </summary>
    public async Task<TaskResult> ConfigureStarterAsync(
        string agentId,
        Guid accountId,
        bool live,
        Guid operatorId,
        CancellationToken cancellationToken)
    {
        var actorId = operatorId.ToString("D");
        var settings = await configuration.SaveRunSettingsAsync(
            agentId,
            new NhAssistantAgentRunSettingsRequest(
                NhAssistantAccountabilities.ServiceAccount,
                accountId.ToString("D"),
                ApprovalPolicy: SampleIntakeReviewerComposition.ApprovalPolicyId,
                RuleHandler: SampleProjectStarterComposition.KickoffRules,
                MaxRunsPerSubjectPerDay: 2,
                Reason: "Start new projects"),
            actorId,
            cancellationToken);
        if (!settings.Success)
        {
            return settings;
        }

        var trigger = await configuration.SaveTriggerAsync(
            agentId,
            StarterTriggerId,
            new NhAssistantDataTriggerRequest(
                NhAssistantDataTriggerSources.Event,
                Event: SampleProjectStarterComposition.ProjectCreatedEvent,
                Conflict: NhAssistantTriggerConflictNames.Ignore,
                Reason: "Start new projects"),
            actorId,
            cancellationToken);
        if (!trigger.Success || !live)
        {
            return trigger;
        }

        return await configuration.SetTriggerShadowAsync(agentId, StarterTriggerId, false, actorId, "Start new projects", cancellationToken);
    }

    /// <summary>
    /// SPM-296: the agent only chats again: its trigger and its run settings are removed.
    /// </summary>
    public async Task<TaskResult> RemoveStarterAsync(string agentId, Guid operatorId, CancellationToken cancellationToken)
    {
        var actorId = operatorId.ToString("D");
        var trigger = await configuration.RemoveTriggerAsync(agentId, StarterTriggerId, null, actorId, cancellationToken: cancellationToken);
        if (!trigger.Success && !HasCode(trigger, NhAssistantErrorCodes.TriggerNotFound))
        {
            return trigger;
        }

        var settings = await configuration.RemoveRunSettingsAsync(agentId, null, actorId, cancellationToken: cancellationToken);
        return settings.Success || HasCode(settings, NhAssistantErrorCodes.AgentNotConfigured) ? TaskResult.Succeeded() : settings;
    }

    /// <summary>
    /// SPM-311: lets other assistants consult the project starter about projects, as an administrator
    /// does in the administration: it says whether a draft project can start and may propose to start
    /// it. The person who asked decides the proposal in their own chat when they may; otherwise a
    /// project approver other than that person decides it and it executes with the rights of the
    /// operations account the starter answers to. The consultation is in effect at once; the project
    /// assistant, whose tool selectors name its consult tool, sees the tool from its next turn on.
    /// </summary>
    public Task<TaskResult<NhAssistantDataConsultationDto>> MakeStarterConsultableAsync(
        string agentId,
        Guid operatorId,
        CancellationToken cancellationToken)
    {
        return configuration.SaveConsultationAsync(
            agentId,
            ProjectSubjects.Type,
            StarterConsultation(),
            operatorId.ToString("D"),
            cancellationToken);
    }

    /// <summary>
    /// SPM-311: what the starter's consultation says and allows.
    /// </summary>
    public static NhAssistantDataConsultationRequest StarterConsultation()
    {
        return new NhAssistantDataConsultationRequest(
            "Knows whether a draft project can start and can propose to start it.",
            Instructions: "You are asked whether a draft project can start. Answer in one or two sentences; "
                          + "when it can start, propose to make it active.",
            Routes:
            [
                NhAssistantConsultationRoutes.AskerDecides,
                NhAssistantConsultationRoutes.SubjectAgentRights
            ],
            ProposalPolicy: SampleProjectStarterComposition.ConsultationPolicyId,
            Reason: "Let the project assistant ask the starter")
        {
            // The administrator chooses the starter's tools for this consultation, within its own
            // selectors: a tool outside them is refused with toolSelectors: not-allowed.
            ToolSelectors = [ProjectAiTools.ChangeStatusToolId]
        };
    }

    /// <summary>
    /// SPM-311: takes the starter's consultation out of effect; proposals it left can still be decided.
    /// </summary>
    public async Task<TaskResult> RemoveStarterConsultationAsync(string agentId, Guid operatorId, CancellationToken cancellationToken)
    {
        var removed = await configuration.RemoveConsultationAsync(agentId, ProjectSubjects.Type, null, operatorId.ToString("D"), cancellationToken: cancellationToken);
        return removed.Success || HasCode(removed, NhAssistantErrorCodes.ConsultationNotFound) ? TaskResult.Succeeded() : removed;
    }

    private static bool HasCode(TaskResult result, string code)
    {
        return result.GetResultItems().Any(item => item.Name == code);
    }
}
