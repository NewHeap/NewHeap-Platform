using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using NewHeap.Platform.AI;
using NewHeap.Platform.AI.Chat;
using NewHeap.Platform.AI.Chat.AspNet;
using SampleProjectManagement.Core.Events;
using SampleProjectManagement.Core.Services;
using SampleProjectManagement.DAL;
using SampleProjectManagement.DAL.Entities;

namespace SampleProjectManagement.Api.Composition;

/// <summary>
/// SPM-286: composes the project planner, one agent that acts as a broad role through two skills.
/// The task follow-up skill works by rules: each completed task starts it through its trigger, and
/// its rules say what is left without a model. The deadline-risk skill works with the model, its
/// own instructions and only the status tool. A daily deadline check names no skill: a cheap triage
/// call reads the project's briefing and sends the check to the deadline skill, or ends it with
/// nothing to do. Each skill has its own budget and state, so stopping the deadline skill keeps the
/// task follow-up working. Call it after <c>AddSampleIntakeReviewer</c>, which registers the project
/// subject type and its briefing.
/// SPM-287: when the follow-up finds every task of a project with a deadline completed, its rules
/// hand the project to the project closer instead of proposing the closer's work themselves. The
/// closer answers in its own thread; the planner hears back how it ended, with the closer's summary.
/// SPM-292: the task follow-up skill works by rules first. A task completed after the project's
/// deadline is a case its rules cannot judge: they hand it over, and the skill's own model judges it
/// with the skill's instructions and only the status tool. After people decide its proposal, the
/// follow-up run continues in the same skill, whose rules take the decision as the answer.
/// SPM-290: the planner works for the project's owner and reads the project's data through the API
/// bridge gateway in its headless runs, with the owner's rights: every call gets a short-lived
/// delegated read credential of the owner, which this API's own bearer scheme validates and the
/// library restricts to the gateway's GET calls.
/// SPM-295: the building blocks that skills and triggers defined as data name: the completed-task
/// event, the active projects with a deadline soon and the progress-note rules. An administrator
/// combines them into a skill of the planner and its trigger at runtime, without a deploy.
/// SPM-297: the progress-percentage rules are a second way to note progress; a changed progress note
/// that names them is evaluated in shadow next to the live note before it is promoted.
/// SPM-308: the status-changed event offers the named filter <c>not-own-change</c>, which a data
/// trigger selects so the planner does not start itself again on a project whose status its own tool
/// changed there. The status tool stamps the agent and the subject it works in on the event; the filter
/// skips the event only when both are the trigger's agent and subject, so the planner's change made
/// while it worked on another subject, and every person's change, still start it.
/// SPM-313: the planner's work is booked on cost center CC-4100, set in code as agent metadata; an
/// administrator may book it elsewhere in the administration, which overrides the value in code.
/// </summary>
public static class SampleProjectPlannerComposition
{
    public const string AgentId = "sample-project-planner";
    public const string TaskFollowUpSkill = "task-follow-up";
    public const string DeadlineRiskSkill = "deadline-risk";
    public const string TaskTriggerId = "project-task-completed-planner";
    public const string DeadlineCheckTriggerId = "daily-deadline-check";
    public const string TriageProfileName = "project-planner-triage";
    public const string FollowUpPolicyId = "project-planning";

    /// <summary>
    /// SPM-295: the names data refers to.
    /// </summary>
    public const string TaskCompletedEvent = "project.task-completed";
    public const string ActiveProjectsSet = "active-projects-with-deadline-soon";
    public const string ProgressNoteRules = "project-progress-note";
    public const string ProgressPercentageRules = "project-progress-percentage";

    /// <summary>
    /// SPM-308: the status-changed event and its filter that skips an agent's own change.
    /// </summary>
    public const string StatusChangedEvent = "project.status-changed";
    public const string NotOwnChangeFilter = "not-own-change";

    public static IServiceCollection AddSampleProjectPlanner(this IServiceCollection services)
    {
        // Triage needs no tools: one short call with structured output decides the skill.
        services.AddNewHeapPlatformAI(ai => ai
            .AddChatProfile(TriageProfileName, profile => profile
                .UseKeyedClient(SampleAssistantComposition.ModelKey)
                .WithStreaming(NhAiStreamingPolicy.Allowed)
                .PermitDataClassifications(NhAiDataClassification.Internal)
                // The region of the configured real model as well, since triage calls the same model.
                .PermitExecutionRegions(SampleAssistantComposition.ModelExecutionRegions(services))
                .WithBudget(maxInputTokens: 4_096, maxOutputTokens: 256, maxCalls: 1)
                .WithTimeout(TimeSpan.FromSeconds(15))));

        services.AddNewHeapAssistant(assistant => assistant
            // SPM-292: proposals of the task follow-up skill; a decision starts a follow-up run that
            // continues in the skill.
            .AddApprovalPolicy(FollowUpPolicyId, policy => policy
                .Mode(NhAiApprovalMode.Deferred)
                .Lifetime(TimeSpan.FromDays(3))
                .Approvers(NhAiApprovers.SubjectRole(NhAiSubjectRole.Approver))
                .OnExpiry(NhAiApprovalExpiryAction.Withdraw)
                .StartFollowUpRun())
            .AddAgent(new NhAssistantAgentDefinition(
                Id: AgentId,
                Version: 1,
                DisplayNameKey: "nh-assistant.agents.sample-project-planner.name",
                DescriptionKey: "nh-assistant.agents.sample-project-planner.description",
                ProfileName: SampleAssistantComposition.ProfileName,
                Instructions: ProjectAiAssets.ProjectPlannerInstructions,
                // SPM-290: the second key of gateway reads; AllowGatewayReads below is the first.
                ToolSelectors: [ProjectAiTools.ChangeStatusToolId, SampleAiBridgeComposition.GatewayToolSetId + ".*"],
                Autonomy: NhAiAutonomyLevel.Execute,
                RequiredPolicy: SampleAssistantComposition.AccessPolicy))
            // SPM-304: the planner belongs to the project management group, whose board the portal shows.
            .AddSampleProjectManagementGroup()
            // SPM-313: the application's cost center of agents, registered once with the planner.
            .AddSampleAgentCostCenter()
            // SPM-313: the departments agents work for, each found by any of its codes (IndexByEach).
            .AddSampleAgentDepartments()
            .ConfigureAgent(AgentId, agent => agent
                // SPM-313: the cost center the planner's work is booked on, checked at startup.
                .Metadata(SampleAgentCostCenterComposition.Entry, SampleAgentCostCenterComposition.PlannerCostCenter)
                .Metadata(SampleAgentDepartmentsComposition.Entry, SampleAgentDepartmentsComposition.PlannerDepartments)
                // SPM-302: the planner works on projects.
                .WorksOn(ProjectSubjects.Type)
                .InGroup(SampleAgentGroupComposition.ProjectManagement)
                .Grant(ProjectAiTools.ChangeStatusToolId)
                .TriggerOnly()
                .Accountability(NhAiAgentAccountability.SubjectResponsible)
                .AllowGatewayReads()
                .UseApprovalPolicy(SampleIntakeReviewerComposition.ApprovalPolicyId)
                .Budget(
                    NhAssistantAgentBudget.PerDay(toolCalls: 100, tokens: 300_000),
                    maxConcurrentRuns: 2,
                    maxRunsPerSubjectPerDay: 20)
                .MaxTokensPerRun(40_000)
                .Triage(triage => triage.UseProfile(TriageProfileName))
                .Skill(TaskFollowUpSkill, skill => skill
                    .Description("Follows up completed tasks by rule: says how many tasks are left and which comes next.")
                    .UseRuleHandler<ProjectTaskFollowUpRules>()
                    // SPM-292: what the skill's model knows and may do when its rules hand over.
                    .Instructions(ProjectAiAssets.ProjectPlannerLateTaskInstructions)
                    .Tools(ProjectAiTools.ChangeStatusToolId)
                    .UseApprovalPolicy(FollowUpPolicyId))
                .Skill(DeadlineRiskSkill, skill => skill
                    .Description("Judges whether an active project will miss its deadline and proposes to put it on hold.")
                    .Instructions(ProjectAiAssets.ProjectPlannerDeadlineRiskInstructions)
                    .Tools(ProjectAiTools.ChangeStatusToolId, SampleAiBridgeComposition.GatewayToolSetId + ".*")
                    .MaxTokensPerRun(30_000)
                    .Budget(NhAssistantAgentBudget.PerDay(toolCalls: 20, tokens: 100_000))))
            .AddTrigger<ProjectTaskUpdatedEvent>(TaskTriggerId, trigger => trigger
                .StartAgent(AgentId)
                .Skill(TaskFollowUpSkill)
                // Only completed tasks concern the follow-up; other updates evaluate nothing more.
                .When(evt => evt.IsCompleted)
                .Subject(ProjectSubjects.Type, evt => ProjectSubjects.IdOf(evt.ProjectId))
                .Message(evt => $"Task {evt.ProjectTaskId:D} of project {evt.ProjectId:D} was completed.")
                // SPM-287: the rules hand a finished project over only from a run that received a
                // completed task, as the closer's rules check before they propose.
                .Payload(evt => new ProjectTaskChange(evt.ProjectTaskId, evt.IsCompleted))
                .Dedupe(evt => evt.EventId.ToString("N"))
                .OnBusy(NhAssistantTriggerConflict.Coalesce, TimeSpan.Zero))
            .AddScheduledTrigger(DeadlineCheckTriggerId, schedule => schedule
                .StartAgent(AgentId)
                .Cron("0 8 * * 1-5", TimeZoneInfo.FindSystemTimeZoneById("Europe/Amsterdam"))
                .Subjects(ActiveProjectsWithDeadlineSoon)
                .Message(tick => $"Daily deadline check of project {tick.Subject.Id}.")
                .MaxSubjectsPerTick(50)
                .OnBusy(NhAssistantTriggerConflict.Ignore))
            // SPM-295: building blocks for the run configuration as data. The event says what every
            // trigger on it receives, as a trigger in code does; one event serves both.
            .AddTriggerEvent<ProjectTaskUpdatedEvent>(TaskCompletedEvent, evt => evt
                .Description("A task of a project was completed.")
                .When(change => change.IsCompleted)
                // SPM-302: the event names the project type up front; the administration offers it
                // only to agents that work on projects.
                .Subject(ProjectSubjects.Type, change => ProjectSubjects.IdOf(change.ProjectId))
                .Message(change => $"Task {change.ProjectTaskId:D} of project {change.ProjectId:D} was completed.")
                .Payload(change => new ProjectTaskChange(change.ProjectTaskId, change.IsCompleted))
                .Dedupe(change => change.EventId.ToString("N")))
            // SPM-308: a data trigger on a status change selects the filter, so the planner's own
            // change of a project's status does not start it on that project again. The filter
            // runs after the subject function and learns the subject the trigger fires for.
            .AddTriggerEvent<ProjectStatusChangedEvent>(StatusChangedEvent, evt => evt
                .Description("The status of a project changed.")
                .Subject(ProjectSubjects.Type, changed => ProjectSubjects.IdOf(changed.ProjectId))
                .Initiator(changed => changed.ChangedByUserId?.ToString("D"))
                .Message(changed => $"The status of project {changed.ProjectId:D} changed from {changed.PreviousStatus} to {changed.Status}.")
                .Dedupe(changed => changed.EventId.ToString("N"))
                .Filter(
                    NotOwnChangeFilter,
                    "Skips a status change the agent made itself while it worked on this project.",
                    (changed, trigger) => !IsOwnChange(changed, trigger)))
            .AddSubjectSet(ActiveProjectsSet, "Active projects with a deadline in the next two weeks.", ActiveProjectsWithDeadlineSoon)
            .AddRuleHandler<ProjectProgressNoteRules>(ProgressNoteRules, "Notes how many of the project's tasks are completed, without a model.")
            .AddRuleHandler<ProjectProgressPercentageRules>(ProgressPercentageRules, "Notes which share of the project's tasks is completed, without a model."));
        return services;
    }

    /// <summary>
    /// SPM-308: whether the trigger's own agent changed the status while it worked on the subject the
    /// trigger fires for. Cheap and free of I/O, as every filter must be.
    /// </summary>
    private static bool IsOwnChange(ProjectStatusChangedEvent changed, NhAssistantTriggerFilterContext trigger)
    {
        return changed.ChangedByAgentActorId == trigger.AgentActorId
               && changed.ChangedInSubjectType == trigger.Subject.Type
               && changed.ChangedInSubjectId == trigger.Subject.Id;
    }

    private static async IAsyncEnumerable<NhAiSubjectRef> ActiveProjectsWithDeadlineSoon(
        IServiceProvider services,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var dbContext = services.GetRequiredService<SampleProjectManagementDbContext>();
        var horizon = DateTimeOffset.UtcNow.AddDays(14);
        var projects = await dbContext.Projects
            .AsNoTracking()
            .Where(project => project.Status == ProjectStatus.Active && project.Deadline != null && project.Deadline <= horizon)
            .OrderBy(project => project.Deadline)
            .Select(project => project.Id)
            .Take(50)
            .ToListAsync(cancellationToken);
        foreach (var projectId in projects)
        {
            yield return ProjectSubjects.For(projectId);
        }
    }
}

/// <summary>
/// SPM-295: the rules of the progress note, a skill an administrator adds to the planner as data.
/// They count the project's tasks and note how far it is; they propose nothing and call no model.
/// They are registered once by name, so several data skills, or the versions of one, may name them:
/// the context says which skill and version they run for, and the note says which version wrote it.
/// </summary>
public sealed class ProjectProgressNoteRules(
    SampleProjectManagementDbContext dbContext,
    IStringLocalizer<SampleAssistantToolPresenter> localizer) : INhAssistantAgentRuleHandler
{
    public async Task<NhAssistantRuleResult> RunAsync(NhAssistantRuleContext context, CancellationToken cancellationToken)
    {
        var projectId = Guid.Parse(context.Subject.Subject.Id);
        var tasks = dbContext.ProjectTasks.AsNoTracking().Where(task => task.ProjectId == projectId);
        var total = await tasks.CountAsync(cancellationToken);
        var completed = await tasks.CountAsync(task => task.IsCompleted, cancellationToken);
        if (context.SkillVersion is { } version)
        {
            return NhAssistantRuleResult.Done(_ => localizer["PlannerProgressNoteOfVersion", completed, total, version, context.SkillId!].Value);
        }

        return NhAssistantRuleResult.Done(_ => localizer["PlannerProgressNote", completed, total].Value);
    }
}

/// <summary>
/// SPM-297: the rules of a changed progress note: the share of the project's tasks that is completed.
/// </summary>
public sealed class ProjectProgressPercentageRules(
    SampleProjectManagementDbContext dbContext,
    IStringLocalizer<SampleAssistantToolPresenter> localizer) : INhAssistantAgentRuleHandler
{
    public async Task<NhAssistantRuleResult> RunAsync(NhAssistantRuleContext context, CancellationToken cancellationToken)
    {
        var projectId = Guid.Parse(context.Subject.Subject.Id);
        var tasks = dbContext.ProjectTasks.AsNoTracking().Where(task => task.ProjectId == projectId);
        var total = await tasks.CountAsync(cancellationToken);
        var completed = await tasks.CountAsync(task => task.IsCompleted, cancellationToken);
        var percentage = total == 0 ? 0 : completed * 100 / total;
        return NhAssistantRuleResult.Done(_ => localizer["PlannerProgressPercentage", percentage].Value);
    }
}

/// <summary>
/// SPM-286: the rules of the planner's task follow-up skill. They read the project's tasks as they
/// are now and say what is left; they propose nothing and call no model.
/// SPM-287: a project with a deadline whose tasks are all completed goes to the project closer, and
/// the closer's answer comes back to this skill. Only a run that received a completed task hands it
/// over: when tasks are completed in quick succession, one run may take every waiting event, and a
/// run that took none would otherwise hand the same project over again.
/// </summary>
public sealed class ProjectTaskFollowUpRules(
    SampleProjectManagementDbContext dbContext,
    IStringLocalizer<SampleAssistantToolPresenter> localizer) : INhAssistantAgentRuleHandler
{
    public async Task<NhAssistantRuleResult> RunAsync(NhAssistantRuleContext context, CancellationToken cancellationToken)
    {
        if (context.Inputs.LastOrDefault(input => input.TriggerId == NhAssistantTriggerIds.HandOffEnded)
                ?.GetPayload<NhAssistantHandOffResult>() is { } ended)
        {
            // The closer's summary is data from another agent; the rules only pass it on, shortened
            // to fit a rule summary.
            return NhAssistantRuleResult.Done(_ => ended.Summary is { } summary
                ? localizer["PlannerCloserAnswered", summary.Length > 1_500 ? summary[..1_500] + "…" : summary].Value
                : localizer["PlannerCloserEnded", ended.Status].Value);
        }

        // SPM-292: the follow-up of a proposal that people decided continues in this skill. The
        // decision is the answer, so the rules do not judge the project again.
        if (context.Inputs.Any(input => input.TriggerId is NhAssistantTriggerIds.ProposalExecuted or NhAssistantTriggerIds.ProposalDecided))
        {
            return NhAssistantRuleResult.Done(_ => localizer["PlannerDecisionFollowedUp"].Value);
        }

        var projectId = Guid.Parse(context.Subject.Subject.Id);
        var open = await dbContext.ProjectTasks
            .AsNoTracking()
            .Where(task => task.ProjectId == projectId && !task.IsCompleted)
            .OrderBy(task => task.Title)
            .Select(task => task.Title)
            .ToListAsync(cancellationToken);
        var total = await dbContext.ProjectTasks.CountAsync(task => task.ProjectId == projectId, cancellationToken);
        if (open.Count > 0)
        {
            // SPM-292: work that goes on after the deadline is a case the rules cannot judge. They
            // hand it to the skill's model, which works with the skill's instructions and tools.
            var now = DateTimeOffset.UtcNow;
            var passed = await dbContext.Projects
                .AsNoTracking()
                .Where(project => project.Id == projectId
                    && project.Status == ProjectStatus.Active
                    && project.Deadline != null
                    && project.Deadline < now)
                .Select(project => project.Deadline)
                .FirstOrDefaultAsync(cancellationToken);
            if (passed is { } lateSince)
            {
                return NhAssistantRuleResult.HandOver(
                    $"A task was completed after the project's deadline of {lateSince:yyyy-MM-dd}, and {open.Count} of {total} tasks are still open. Judge whether to put the project on hold.");
            }
            return NhAssistantRuleResult.Done(_ => localizer["PlannerFollowUp", open.Count, total, open[0]].Value);
        }

        var deadline = await dbContext.Projects
            .AsNoTracking()
            .Where(project => project.Id == projectId && project.Status == ProjectStatus.Active)
            .Select(project => project.Deadline)
            .FirstOrDefaultAsync(cancellationToken);
        if (deadline is null)
        {
            return NhAssistantRuleResult.Done(_ => localizer["PlannerAllDone", total].Value);
        }

        // The run's inputs all concern its project. Without a completed task of its own, the run
        // that received the completion hands the project over; this run does not do it again.
        if (!context.Inputs.Any(input => input.GetPayload<ProjectTaskChange>()?.IsCompleted == true))
        {
            return NhAssistantRuleResult.Done(_ => localizer["PlannerNoTaskCompleted", total].Value);
        }

        // The closer decides whether the project can be completed, in its own thread and under its
        // own approval policy; the planner does not propose the closer's work itself.
        var handOff = await context.HandOffAsync(
            SampleProjectCloserComposition.AgentId,
            context.Subject.Subject,
            $"All {total} tasks of the project are completed and its deadline is {deadline.Value:yyyy-MM-dd}. Check whether it can be completed.",
            cancellationToken: cancellationToken);
        return NhAssistantRuleResult.Done(_ => handOff.Accepted
            ? localizer["PlannerHandedToCloser", total].Value
            : localizer["PlannerAllDone", total].Value);
    }
}
