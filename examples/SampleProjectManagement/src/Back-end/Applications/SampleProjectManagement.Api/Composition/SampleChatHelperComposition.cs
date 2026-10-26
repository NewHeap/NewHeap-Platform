using NewHeap.Platform.AI;
using NewHeap.Platform.AI.Chat;
using NewHeap.Platform.AI.Chat.AspNet;
using NewHeap.Platform.AI.Chat.AspNet.AgentRuns;
using SampleProjectManagement.Core.Services;

namespace SampleProjectManagement.Api.Composition;

/// <summary>
/// SPM-343: the chat helper works in people's chats only (<c>ChatOnly()</c>). It keeps the run
/// settings a chat uses: its meeting notes skill, offered with <c>use_skill</c> (<c>SkillsInChat</c>),
/// structured questions (<c>AllowQuestions</c>) and a limit on the consultations of the intake
/// reviewer it starts in one turn. It works on no subject, so it is on no project's page, a start on a
/// project is refused with <c>assistant-agent-does-not-work-on</c>, nothing triggers it and it needs no
/// background operations of its own. The host would refuse to start if it also said what it works on,
/// accepted hand-offs or worked in shadow.
/// Call it after <c>AddSampleIntakeReviewer</c>, whose consultation the helper may ask.
/// </summary>
public static class SampleChatHelperComposition
{
    public const string AgentId = "sample-project-chat-helper";

    /// <summary>
    /// The skill people's chats with the helper load with <c>use_skill</c>.
    /// </summary>
    public const string MeetingNotesSkill = "meeting-notes";

    /// <summary>
    /// The consultations of other agents one turn of the helper may start.
    /// </summary>
    public const int MaxConsultationsPerTurn = 2;

    public static IServiceCollection AddSampleChatHelper(this IServiceCollection services)
    {
        services.AddNewHeapAssistant(assistant => assistant
            .AddAgent(new NhAssistantAgentDefinition(
                Id: AgentId,
                Version: 1,
                DisplayNameKey: "nh-assistant.agents.sample-project-chat-helper.name",
                DescriptionKey: "nh-assistant.agents.sample-project-chat-helper.description",
                ProfileName: SampleAssistantComposition.ProfileName,
                Instructions: ProjectAiAssets.ProjectAgentInstructions,
                ToolSelectors:
                [
                    ProjectAiTools.ChangeStatusToolId,
                    NhAssistantConsultationTools.ConsultToolId(SampleIntakeReviewerComposition.AgentId, ProjectSubjects.Type)
                ],
                Autonomy: NhAiAutonomyLevel.Propose,
                RequiredPolicy: SampleAssistantComposition.AccessPolicy))
            .ConfigureAgent(AgentId, agent => agent
                // SPM-343: chat settings without any subject, start, trigger, hand-off or replay.
                .ChatOnly()
                .Skill(MeetingNotesSkill, skill => skill
                    .Description("Turns what the person tells about a project meeting into short notes and proposes a status when the meeting decided one.")
                    .Instructions("Write the notes as a short list of decisions and open points. Propose a status change only when the meeting decided it.")
                    .Tools(ProjectAiTools.ChangeStatusToolId))
                .SkillsInChat()
                .AllowQuestions()
                .MaxConsultationsPerTurn(MaxConsultationsPerTurn)));
        return services;
    }
}
