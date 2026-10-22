using NewHeap.Platform.AI;
using NewHeap.Platform.AI.Chat;
using NewHeap.Platform.AI.Chat.AspNet;
using SampleProjectManagement.Core.Services;

namespace SampleProjectManagement.Api.Composition;

/// <summary>
/// A document was added to the documents folder of a project in the media library
/// (<see cref="Events.ProjectDocumentEventHandler"/>).
/// </summary>
public sealed record ProjectDocumentAdded(Guid DivisionId, Guid ProjectId, Guid DocumentId);

/// <summary>
/// SPM-306: composes the document reader, an agent that reads the documents of a project when one is
/// added. Agents read no files until they opt in: the reader accepts files with
/// <c>AcceptsFiles()</c>, and so does its <see cref="ReadDocumentsSkill"/>, whose model calls the
/// document tool. The tool's result names the media items; the assistant opens them through the
/// application's file source for the agent, which may read the documents of the project it works on,
/// and the model reads a PDF itself and a Word or Excel document as the text of the example reader.
/// The other agents of the sample accept no files. SPM-307: project documents are confidential, so the
/// skill classifies its model calls with <c>DataClassification(Confidential)</c>; the agent's chat
/// profile permits internal data only, and the profile resolution moves every model call of the skill
/// to its fallback <see cref="SampleAssistantComposition.ConfidentialProfileName"/>. Call it after
/// <c>AddSampleIntakeReviewer</c>, which registers the project subject type, and
/// <c>AddSampleAssistantFiles</c>, which registers the tool.
/// </summary>
public static class SampleProjectDocumentReaderComposition
{
    public const string AgentId = "sample-project-document-reader";
    public const string ReadDocumentsSkill = "read-documents";
    public const string TriggerId = "project-document-added";

    public static IServiceCollection AddSampleProjectDocumentReader(this IServiceCollection services)
    {
        services.AddNewHeapAssistant(assistant => assistant
            .AddAgent(new NhAssistantAgentDefinition(
                Id: AgentId,
                Version: 1,
                DisplayNameKey: "nh-assistant.agents.sample-project-document-reader.name",
                DescriptionKey: "nh-assistant.agents.sample-project-document-reader.description",
                ProfileName: SampleAssistantComposition.ProfileName,
                Instructions: ProjectAiAssets.ProjectDocumentReaderInstructions,
                ToolSelectors: [ProjectDocumentAiTools.ReadToolId],
                Autonomy: NhAiAutonomyLevel.Observe,
                RequiredPolicy: SampleAssistantComposition.AccessPolicy))
            .ConfigureAgent(AgentId, agent => agent
                .WorksOn(ProjectSubjects.Type)
                .Grant(ProjectDocumentAiTools.ReadToolId)
                .TriggerOnly()
                // The agent takes files: from its tool in a run, and from people who attach them to a
                // message in its thread.
                .AcceptsFiles()
                .Budget(
                    NhAssistantAgentBudget.PerDay(toolCalls: 20, tokens: 200_000),
                    maxConcurrentRuns: 2,
                    maxRunsPerSubjectPerDay: 10)
                .Skill(ReadDocumentsSkill, skill => skill
                    .Description("Reads the newest documents of a project and says what they change for the project.")
                    .Instructions(ProjectAiAssets.ProjectDocumentReadingInstructions)
                    .Tools(ProjectDocumentAiTools.ReadToolId)
                    // A skill takes files only when it opts in as well, and may lower the limits.
                    .AcceptsFiles(limits => limits.MaxFileBytes = 10 * 1024 * 1024)
                    // SPM-307: what the documents say is confidential, more than the rest of the
                    // agent's work: the skill's model calls go to the profile that permits it.
                    .DataClassification(NhAiDataClassification.Confidential)))
            .AddTrigger<ProjectDocumentAdded>(TriggerId, trigger => trigger
                .StartAgent(AgentId)
                .Skill(ReadDocumentsSkill)
                .Subject(ProjectSubjects.Type, evt => ProjectSubjects.IdOf(evt.ProjectId))
                .Message(evt =>
                    $"Document {evt.DocumentId:D} was added to project {evt.ProjectId:D}. Read the project's documents and say what the new one changes.")
                .Dedupe(evt => evt.DocumentId.ToString("N"))
                .OnBusy(NhAssistantTriggerConflict.Coalesce, TimeSpan.Zero)));
        return services;
    }
}
