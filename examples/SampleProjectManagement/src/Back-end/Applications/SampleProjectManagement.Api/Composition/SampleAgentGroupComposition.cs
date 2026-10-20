using NewHeap.Platform.AI.Chat;

namespace SampleProjectManagement.Api.Composition;

/// <summary>
/// SPM-304: the "Project management" group of agents, registered in code with its name and description
/// in English and Dutch from the application's resources. The management portal's project management page shows its board with the
/// <c>nh-assistant-agent-board</c> component. The project closer and the project planner join it in
/// code with <c>InGroup</c>; the project follow-up, created as data, joins it in the administration,
/// as the development seeder does. Every composition whose agents join the group registers it: the
/// same group registered twice is kept.
/// </summary>
public static class SampleAgentGroupComposition
{
    public const string ProjectManagement = "project-management";

    public static NhAssistantBuilder AddSampleProjectManagementGroup(this NhAssistantBuilder assistant)
    {
        ArgumentNullException.ThrowIfNull(assistant);
        // The names and the description come from the application's English and Dutch resources.
        return assistant.AddAgentGroup(ProjectManagement, group => group
            .Named(SampleSubjectTypeNames.Of("AgentGroupProjectManagement"), fallbackLanguage: "en")
            .Describe(SampleSubjectTypeNames.Of("AgentGroupProjectManagementDescription")));
    }
}
