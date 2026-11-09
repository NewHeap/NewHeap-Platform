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
/// People may still chat with it about a project (<c>ChatsAbout</c>): the project dialog offers that chat,
/// <c>GET agents?subjectType=project&amp;subjectId=</c> lists the helper, and the project's access resolver
/// decides as for any chat about a project, on creation and on every turn, read and invitation. That
/// reaches chats only: the helper still works on no project and every start stays refused.
/// SPM-333: execute autonomy offers the skill its status tool in these chats. Every change still waits
/// for the person's approval, and the decision continues the turn in the skill that proposed it.
/// SPM-349: the helper only proposes a change to a project it read in the same turn
/// (<see cref="ProjectReadBeforeChangeProposalGuard"/>), so a made-up id reaches no card.
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
                    ProjectOverviewAiTools.GetToolId,
                    NhAssistantConsultationTools.ConsultToolId(SampleIntakeReviewerComposition.AgentId, ProjectSubjects.Type)
                ],
                Autonomy: NhAiAutonomyLevel.Execute,
                RequiredPolicy: SampleAssistantComposition.AccessPolicy))
            // SPM-349: a value the model proposes has to come from what a tool returned in this turn.
            .AddProposalGuard<ProjectReadBeforeChangeProposalGuard>(
                [ProjectAiTools.ChangeStatusToolId],
                NhAiProposalGuardStages.Proposal)
            .ConfigureAgent(AgentId, agent => agent
                // SPM-343: chat settings without any start, trigger, hand-off or replay.
                .ChatOnly()
                .ChatsAbout(ProjectSubjects.Type)
                .Skill(MeetingNotesSkill, skill => skill
                    .Description("Turns what the person tells about a project meeting into short notes and proposes a status when the meeting decided one.")
                    .Instructions("Write the notes as a short list of decisions and open points. Propose a status change only when the meeting decided it.")
                    .Tools(ProjectAiTools.ChangeStatusToolId, ProjectOverviewAiTools.GetToolId))
                .SkillsInChat()
                .AllowQuestions()
                .MaxConsultationsPerTurn(MaxConsultationsPerTurn)));
        return services;
    }
}
