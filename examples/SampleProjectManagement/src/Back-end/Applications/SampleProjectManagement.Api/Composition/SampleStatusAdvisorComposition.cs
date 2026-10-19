using NewHeap.Platform.AI;
using NewHeap.Platform.AI.Chat;
using NewHeap.Platform.AI.Chat.AspNet;
using SampleProjectManagement.Core.Services;

namespace SampleProjectManagement.Api.Composition;

/// <summary>
/// Composes the status advisor, an agent that a project participant starts on a project with
/// <c>POST threads/start</c>. It has no grants of its own: it acts with the rights of the person
/// who started it (<c>DelegateFromInitiator</c>), checked again in the background without a
/// request. Its status proposals are inline: the run pauses as a waiting background operation
/// until that person approves, then continues and changes the status itself.
/// Call it after <c>AddSampleIntakeReviewer</c>, which registers the project subject type.
/// </summary>
public static class SampleStatusAdvisorComposition
{
    public const string AgentId = "sample-project-status-advisor";
    public const string ApprovalPolicyId = "project-status-inline";

    public static IServiceCollection AddSampleStatusAdvisor(this IServiceCollection services)
    {
        services.AddNewHeapAssistant(assistant => assistant
            .AddAgent(new NhAssistantAgentDefinition(
                Id: AgentId,
                Version: 1,
                DisplayNameKey: "nh-assistant.agents.sample-project-status-advisor.name",
                DescriptionKey: "nh-assistant.agents.sample-project-status-advisor.description",
                ProfileName: SampleAssistantComposition.ProfileName,
                Instructions: ProjectAiAssets.ProjectAgentInstructions,
                ToolSelectors: [ProjectAiTools.ChangeStatusToolId],
                Autonomy: NhAiAutonomyLevel.Execute,
                RequiredPolicy: SampleAssistantComposition.AccessPolicy))
            .AddApprovalPolicy(ApprovalPolicyId, policy => policy
                .Mode(NhAiApprovalMode.Inline)
                .Lifetime(TimeSpan.FromHours(4))
                // The person who started the advisor decides its proposals.
                .Approvers(NhAiApprovers.Initiator)
                .OnExpiry(NhAiApprovalExpiryAction.FailRun))
            .ConfigureAgent(AgentId, agent => agent
                // SPM-302: people start the advisor on a project only.
                .WorksOn(ProjectSubjects.Type)
                .DelegateFromInitiator()
                .UseApprovalPolicy(ApprovalPolicyId)
                .Retry(2)
                .Budget(
                    NhAssistantAgentBudget.PerDay(toolCalls: 50, tokens: 200_000),
                    maxConcurrentRuns: 2,
                    maxRunsPerSubjectPerDay: 10)));
        return services;
    }
}
