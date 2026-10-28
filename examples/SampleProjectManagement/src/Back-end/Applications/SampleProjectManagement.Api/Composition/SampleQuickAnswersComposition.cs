using NewHeap.Platform.AI;
using NewHeap.Platform.AI.Chat;
using NewHeap.Platform.AI.Chat.AspNet;
using SampleProjectManagement.Core.Services;

namespace SampleProjectManagement.Api.Composition;

/// <summary>
/// SPM-359: the quick answers agent answers short questions about the projects of the active division
/// in people's chats. It runs on <see cref="SampleAssistantComposition.InternalProfileName"/>, a profile
/// for a model that may receive internal data only and has no fallback. People's uploads are
/// confidential, so no profile of its chain may receive them: <c>GET agents</c>, the status and the
/// application's channel offer it no attachments, and an upload to its chat is refused with
/// <c>assistant-files-not-accepted</c> instead of failing later in the turn. Call it after
/// <c>AddSampleAssistant</c>, which registers the profile.
/// </summary>
public static class SampleQuickAnswersComposition
{
    public const string AgentId = "sample-project-quick-answers";

    public static IServiceCollection AddSampleQuickAnswers(this IServiceCollection services)
    {
        services.AddNewHeapAssistant(assistant => assistant
            .AddAgent(new NhAssistantAgentDefinition(
                Id: AgentId,
                Version: 1,
                DisplayNameKey: "nh-assistant.agents.sample-project-quick-answers.name",
                DescriptionKey: "nh-assistant.agents.sample-project-quick-answers.description",
                ProfileName: SampleAssistantComposition.InternalProfileName,
                Instructions: ProjectAiAssets.ProjectAgentInstructions,
                ToolSelectors: ["projects.search"],
                Autonomy: NhAiAutonomyLevel.Explain,
                RequiredPolicy: SampleAssistantComposition.AccessPolicy)));
        return services;
    }
}
