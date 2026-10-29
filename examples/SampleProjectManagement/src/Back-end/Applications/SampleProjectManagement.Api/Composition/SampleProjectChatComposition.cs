using NewHeap.Platform.AI;
using NewHeap.Platform.AI.Chat;
using NewHeap.Platform.AI.Chat.AspNet;
using SampleProjectManagement.Core.Services;

namespace SampleProjectManagement.Api.Composition;

/// <summary>
/// SPM-363: the project chat answers a person's questions about one project, in a chat about that
/// project. It works on projects and is offered only for chats about a subject
/// (<see cref="NhAssistantAgentRunBuilder.RequireChatSubject"/>): <c>GET agents</c> and the status leave it
/// out, <c>GET agents?subjectType=project&amp;subjectId=</c> lists it for a project the person may read, and
/// the project dialog starts the chat with <c>NhAssistantStore.setChatSubject</c>, so
/// <c>POST conversations</c> names the project. The project's access resolver decides when the chat is
/// created and again on every turn, read and invitation: a member who is removed from the project
/// loses the chat at once. Each message reads the project's briefing
/// (<see cref="ProjectBriefingProvider"/>, purpose <c>chat</c>), and its tools run with the person's own
/// rights. The chat stays the person's; it is not the agents' thread about the project. Call it after
/// <c>AddSampleAssistant</c>, which registers the project subject type and the profile.
/// </summary>
public static class SampleProjectChatComposition
{
    public const string AgentId = "sample-project-chat";

    public static IServiceCollection AddSampleProjectChat(this IServiceCollection services)
    {
        services.AddNewHeapAssistant(assistant => assistant
            .AddAgent(new NhAssistantAgentDefinition(
                Id: AgentId,
                Version: 1,
                DisplayNameKey: "nh-assistant.agents.sample-project-chat.name",
                DescriptionKey: "nh-assistant.agents.sample-project-chat.description",
                ProfileName: SampleAssistantComposition.ProfileName,
                Instructions: ProjectAiAssets.ProjectAgentInstructions,
                ToolSelectors: ["projects.search"],
                Autonomy: NhAiAutonomyLevel.Explain,
                RequiredPolicy: SampleAssistantComposition.AccessPolicy))
            .ConfigureAgent(AgentId, run => run
                .WorksOn(ProjectSubjects.Type)
                .RequireChatSubject()));
        return services;
    }
}
