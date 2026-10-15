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
/// </summary>
public static class SampleProjectPlannerComposition
{
    public const string AgentId = "sample-project-planner";
    public const string TaskFollowUpSkill = "task-follow-up";
    public const string DeadlineRiskSkill = "deadline-risk";
    public const string TaskTriggerId = "project-task-completed-planner";
    public const string DeadlineCheckTriggerId = "daily-deadline-check";
    public const string TriageProfileName = "project-planner-triage";

    public static IServiceCollection AddSampleProjectPlanner(this IServiceCollection services)
    {
        // Triage needs no tools: one short call with structured output decides the skill.
        services.AddNewHeapPlatformAI(ai => ai
            .AddChatProfile(TriageProfileName, profile => profile
                .UseKeyedClient(SampleAssistantComposition.ModelKey)
                .WithStreaming(NhAiStreamingPolicy.Allowed)
                .PermitDataClassifications(NhAiDataClassification.Internal)
                .PermitExecutionRegions("local")
                .WithBudget(maxInputTokens: 4_096, maxOutputTokens: 256, maxCalls: 1)
                .WithTimeout(TimeSpan.FromSeconds(15))));

        services.AddNewHeapAssistant(assistant => assistant
            .AddAgent(new NhAssistantAgentDefinition(
                Id: AgentId,
                Version: 1,
                DisplayNameKey: "nh-assistant.agents.sample-project-planner.name",
                DescriptionKey: "nh-assistant.agents.sample-project-planner.description",
                ProfileName: SampleAssistantComposition.ProfileName,
                Instructions: ProjectAiAssets.ProjectPlannerInstructions,
                ToolSelectors: [ProjectAiTools.ChangeStatusToolId],
                Autonomy: NhAiAutonomyLevel.Execute,
                RequiredPolicy: SampleAssistantComposition.AccessPolicy))
            .ConfigureAgent(AgentId, agent => agent
                .Grant(ProjectAiTools.ChangeStatusToolId)
                .TriggerOnly()
                .UseApprovalPolicy(SampleIntakeReviewerComposition.ApprovalPolicyId)
                .Budget(
                    NhAssistantAgentBudget.PerDay(toolCalls: 100, tokens: 300_000),
                    maxConcurrentRuns: 2,
                    maxRunsPerSubjectPerDay: 20)
                .MaxTokensPerRun(40_000)
                .Triage(triage => triage.UseProfile(TriageProfileName))
                .Skill(TaskFollowUpSkill, skill => skill
                    .Description("Follows up completed tasks by rule: says how many tasks are left and which comes next.")
                    .UseRuleHandler<ProjectTaskFollowUpRules>())
                .Skill(DeadlineRiskSkill, skill => skill
                    .Description("Judges whether an active project will miss its deadline and proposes to put it on hold.")
                    .Instructions(ProjectAiAssets.ProjectPlannerDeadlineRiskInstructions)
                    .Tools(ProjectAiTools.ChangeStatusToolId)
                    .MaxTokensPerRun(30_000)
                    .Budget(NhAssistantAgentBudget.PerDay(toolCalls: 20, tokens: 100_000))))
            .AddTrigger<ProjectTaskUpdatedEvent>(TaskTriggerId, trigger => trigger
                .StartAgent(AgentId)
                .Skill(TaskFollowUpSkill)
                // Only completed tasks concern the follow-up; other updates evaluate nothing more.
                .When(evt => evt.IsCompleted)
                .Subject(evt => ProjectSubjects.For(evt.ProjectId))
                .Message(evt => $"Task {evt.ProjectTaskId:D} of project {evt.ProjectId:D} was completed.")
                .Dedupe(evt => evt.EventId.ToString("N"))
                .OnBusy(NhAssistantTriggerConflict.Coalesce, TimeSpan.Zero))
            .AddScheduledTrigger(DeadlineCheckTriggerId, schedule => schedule
                .StartAgent(AgentId)
                .Cron("0 8 * * 1-5", TimeZoneInfo.FindSystemTimeZoneById("Europe/Amsterdam"))
                .Subjects(ActiveProjectsWithDeadlineSoon)
                .Message(tick => $"Daily deadline check of project {tick.Subject.Id}.")
                .MaxSubjectsPerTick(50)
                .OnBusy(NhAssistantTriggerConflict.Ignore)));
        return services;
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
/// SPM-286: the rules of the planner's task follow-up skill. They read the project's tasks as they
/// are now and say what is left; they propose nothing and call no model.
/// </summary>
public sealed class ProjectTaskFollowUpRules(
    SampleProjectManagementDbContext dbContext,
    IStringLocalizer<SampleAssistantToolPresenter> localizer) : INhAssistantAgentRuleHandler
{
    public async Task<NhAssistantRuleResult> RunAsync(NhAssistantRuleContext context, CancellationToken cancellationToken)
    {
        var projectId = Guid.Parse(context.Subject.Subject.Id);
        var open = await dbContext.ProjectTasks
            .AsNoTracking()
            .Where(task => task.ProjectId == projectId && !task.IsCompleted)
            .OrderBy(task => task.Title)
            .Select(task => task.Title)
            .ToListAsync(cancellationToken);
        var total = await dbContext.ProjectTasks.CountAsync(task => task.ProjectId == projectId, cancellationToken);
        return NhAssistantRuleResult.Done(open.Count == 0
            ? localizer["PlannerAllDone", total].Value
            : localizer["PlannerFollowUp", open.Count, total, open[0]].Value);
    }
}
