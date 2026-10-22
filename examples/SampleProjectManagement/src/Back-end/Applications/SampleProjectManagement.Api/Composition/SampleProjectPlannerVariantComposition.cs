using NewHeap.Platform.AI;
using NewHeap.Platform.AI.Chat;
using NewHeap.Platform.AI.Chat.AspNet;
using SampleProjectManagement.Core.Services;

namespace SampleProjectManagement.Api.Composition;

/// <summary>
/// SPM-288: composes a variant of the project planner to evaluate before it goes live. The variant
/// is a shadow of the planner: every trigger of the planner fires for it too, it works in its own
/// threads that only administrators see, notifies nobody and executes nothing. It differs in its
/// triage: a smaller triage profile and a stricter description of the deadline-risk skill, so it
/// sends fewer deadline checks to the model. Administrators compare it with the live planner in the
/// administration, replay the planner's recorded runs into it and export the comparison. Call it
/// after <c>AddSampleProjectPlanner</c>.
/// SPM-284: the variant is deployed off with <c>StartsOff()</c>, so it costs nothing until an
/// administrator switches it on to evaluate it. Only the first start applies it; switching it on
/// replays none of the planner's events from before, and a later deploy never switches it off again.
/// </summary>
public static class SampleProjectPlannerVariantComposition
{
    public const string AgentId = "sample-project-planner-v2";
    public const string TriageProfileName = "project-planner-triage-v2";

    public static IServiceCollection AddSampleProjectPlannerVariant(this IServiceCollection services)
    {
        services.AddNewHeapPlatformAI(ai => ai
            .AddChatProfile(TriageProfileName, profile => profile
                .UseKeyedClient(SampleAssistantComposition.ModelKey)
                .WithStreaming(NhAiStreamingPolicy.Allowed)
                .PermitDataClassifications(NhAiDataClassification.Internal)
                // The region of the configured real model as well, since triage calls the same model.
                .PermitExecutionRegions(SampleAssistantComposition.ModelExecutionRegions(services))
                .WithBudget(maxInputTokens: 2_048, maxOutputTokens: 128, maxCalls: 1)
                .WithTimeout(TimeSpan.FromSeconds(10))));

        services.AddNewHeapAssistant(assistant => assistant
            .AddAgent(new NhAssistantAgentDefinition(
                Id: AgentId,
                Version: 1,
                DisplayNameKey: "nh-assistant.agents.sample-project-planner-v2.name",
                DescriptionKey: "nh-assistant.agents.sample-project-planner-v2.description",
                ProfileName: SampleAssistantComposition.ProfileName,
                Instructions: ProjectAiAssets.ProjectPlannerInstructions,
                ToolSelectors: [ProjectAiTools.ChangeStatusToolId],
                Autonomy: NhAiAutonomyLevel.Execute,
                RequiredPolicy: SampleAssistantComposition.AccessPolicy))
            .ConfigureAgent(AgentId, agent => agent
                // A shadow of the live planner: its events, no notifications, nothing executes.
                .ShadowOf(SampleProjectPlannerComposition.AgentId)
                // SPM-284: off until an administrator switches it on to evaluate it.
                .StartsOff()
                .WorksOn(ProjectSubjects.Type)
                .Grant(ProjectAiTools.ChangeStatusToolId)
                .TriggerOnly()
                .UseApprovalPolicy(SampleIntakeReviewerComposition.ApprovalPolicyId)
                // The variant's usage counts against its own budget, which also bounds replays.
                .Budget(
                    NhAssistantAgentBudget.PerDay(toolCalls: 50, tokens: 100_000),
                    maxConcurrentRuns: 2,
                    maxRunsPerSubjectPerDay: 20)
                .MaxTokensPerRun(20_000)
                .Triage(triage => triage.UseProfile(TriageProfileName))
                // The same skill ids as the planner, so its triggers' skills apply.
                .Skill(SampleProjectPlannerComposition.TaskFollowUpSkill, skill => skill
                    .Description("Follows up completed tasks by rule: says how many tasks are left and which comes next.")
                    .UseRuleHandler<ProjectTaskFollowUpRules>()
                    .Instructions(ProjectAiAssets.ProjectPlannerLateTaskInstructions)
                    .Tools(ProjectAiTools.ChangeStatusToolId))
                .Skill(SampleProjectPlannerComposition.DeadlineRiskSkill, skill => skill
                    .Description("Judges a deadline risk only when less than a third of the project's tasks is completed.")
                    .Instructions(ProjectAiAssets.ProjectPlannerDeadlineRiskInstructions)
                    .Tools(ProjectAiTools.ChangeStatusToolId)
                    .MaxTokensPerRun(15_000))));
        return services;
    }
}
