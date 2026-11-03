using System.Globalization;
using System.Resources;
using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using NewHeap.Platform.AI;
using NewHeap.Platform.AI.Chat;
using NewHeap.Platform.AI.Chat.AspNet;
using SampleProjectManagement.Core.Services;
using SampleProjectManagement.DAL;
using SampleProjectManagement.DAL.Entities;

namespace SampleProjectManagement.Api.Composition;

/// <summary>
/// SPM-393: rule scripts (RB-1 to RB-6). The document check is an agent without a model whose rules are a
/// script stored as data, which an administrator writes, dry-runs and sets live in the administration: no
/// developer writes a rule handler for it. Every working-day morning a schedule starts it on the open projects
/// whose deadline falls within the next three days, the subject set of its round. Its script reads the
/// project's documents through the governed tool <c>project-documents.read</c>, with the agent's grant; a
/// project that keeps none raises the alert <see cref="KindId"/> for its owner and the members who work on it,
/// and asks the review board to look at it through <c>projects.request-review</c>, the one write the code's
/// ceiling lets a version opt in to. That tool needs approval, so the write records a proposal that waits for
/// the project's approvers and changes nothing yet. A project that has a document raises nothing, and the kind
/// resolves the open warning by omission. The code sets the ceiling with <c>UseRuleScriptsAsData</c>: rule
/// scripts for this agent only, one write per run and four eyes, so another administrator than the version's
/// author sets it live, after the latest dry run is shown. The sample's projects have no start date, so the
/// check uses the deadline; the document is what the project misses (RS choice).
/// Call it after <c>AddSampleIntakeReviewer</c>, which registers the project subject type and the review
/// request, and <c>AddSampleAssistantFiles</c>, which registers the documents tool.
/// </summary>
public static class SampleRuleScriptsComposition
{
    public const string AgentId = "sample-document-check";
    public const string KindId = "project-missing-document";
    public const string TriggerId = "project-document-check";

    /// <summary>The front-end translation key of the kind's name.</summary>
    public const string KindNameKey = "nh-assistant.alert-kinds.project-missing-document.name";

    /// <summary>How many days before its deadline a project is checked.</summary>
    public const int DaysBeforeDeadline = 3;

    /// <summary>
    /// The script an administrator saves as the check's rules (SPM-393): the body of an async function that
    /// reads the project's documents, raises the kind when there are none, opts in one write and ends with a
    /// summary. It is data, not code: the administration, an agent package or the agent builder stores it.
    /// </summary>
    public const string DocumentCheckScript = """
        const documents = await tools.project_documents_read_v1({ projectId: subject.id });
        if (!documents.success) {
          handOver("The project's documents could not be read (" + documents.code + ").");
          return;
        }
        if (documents.data.documentCount > 0) {
          done("The project keeps " + documents.data.documentCount + " document(s); nothing to warn about.");
          return;
        }
        alerts.raise({ kind: "project-missing-document", key: "documents", parameters: { project: subject.displayName ?? subject.id } });
        const review = await tools.projects_request_review_v1({ projectId: subject.id });
        done("The project has no document three days before its deadline; warned its people and asked for a review (" + (review.success ? "requested" : review.code) + ").");
        """;

    private static readonly ResourceManager Texts = new(typeof(SampleAssistantToolPresenter));

    public static IServiceCollection AddSampleRuleScripts(this IServiceCollection services)
    {
        services.AddNewHeapAssistant(assistant => assistant
            .AddAgent(new NhAssistantAgentDefinition(
                Id: AgentId,
                Version: 1,
                DisplayNameKey: "nh-assistant.agents.sample-document-check.name",
                DescriptionKey: "nh-assistant.agents.sample-document-check.description",
                ProfileName: SampleAssistantComposition.ProfileName,
                Instructions: ProjectAiAssets.ProjectDeadlineWatchInstructions,
                ToolSelectors: [ProjectDocumentAiTools.ReadToolId, ProjectAiReviewTools.RequestReviewToolId],
                // Propose: its write becomes a proposal whatever the tool asks; the tool needs approval anyway.
                Autonomy: NhAiAutonomyLevel.Propose,
                RequiredPolicy: SampleAssistantComposition.AccessPolicy))
            .AddAlertKind(KindId, kind => kind
                .DisplayName(KindNameKey)
                .Urgency(NhAssistantAlertUrgency.Warning)
                .Title((culture, parameters) => Format("AlertProjectMissingDocumentTitle", culture, parameters["project"]))
                .Text((culture, _) => Format("AlertProjectMissingDocumentText", culture))
                .Recipients(NhAssistantAlertRecipients.SubjectResponsible
                    .Or(NhAssistantAlertRecipients.SubjectMembers(NhAiSubjectRole.Participant)))
                .RepeatAfter(TimeSpan.FromDays(1))
                // A round that finds a document raises nothing, and the open warning is resolved.
                .ResolveWhenNotRaised())
            .ConfigureAgent(AgentId, agent => agent
                .WorksOn(ProjectSubjects.Type)
                .HideFromChat()
                .Grant(ProjectDocumentAiTools.ReadToolId, ProjectAiReviewTools.RequestReviewToolId)
                .UseApprovalPolicy(SampleIntakeReviewerComposition.ApprovalPolicyId)
                .Alerts(KindId))
            // The code's ceiling: this agent may have rule scripts, a version may opt in to the review
            // request as its one write per run, and a version goes live by another administrator.
            .UseRuleScriptsAsData(scripts => scripts
                .ForAgent(AgentId, ceiling => ceiling
                    .AllowWrites(ProjectAiReviewTools.RequestReviewToolId)
                    .MaxWritesPerRun(1))
                .RequireFourEyes())
            .AddScheduledTrigger(TriggerId, schedule => schedule
                .StartAgent(AgentId)
                .Cron("30 7 * * 1-5", SampleDeadlineAlertsComposition.TimeZone)
                .Subjects(ProjectsDueSoon)
                .Message(tick => $"Morning round: project {tick.Subject.Id} is due within {DaysBeforeDeadline} days.")
                .MaxSubjectsPerTick(100)
                .OnBusy(NhAssistantTriggerConflict.Ignore)));
        return services;
    }

    /// <summary>
    /// The open projects whose deadline falls within the next three days: the subjects of the morning
    /// round. The window is captured in UTC, so the query stays server-side on SQL Server and PostgreSQL alike.
    /// </summary>
    private static async IAsyncEnumerable<NhAiSubjectRef> ProjectsDueSoon(
        IServiceProvider services,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var now = services.GetRequiredService<TimeProvider>().GetUtcNow();
        var until = now.AddDays(DaysBeforeDeadline);
        var dbContext = services.GetRequiredService<SampleProjectManagementDbContext>();
        var projectIds = await dbContext.Projects
            .AsNoTracking()
            .Where(project => project.Status != ProjectStatus.Completed
                && project.Status != ProjectStatus.Archived
                && project.Deadline != null
                && project.Deadline >= now
                && project.Deadline < until)
            .OrderBy(project => project.Deadline)
            .ThenBy(project => project.Id)
            .Select(project => project.Id)
            .Take(100)
            .ToListAsync(cancellationToken);
        foreach (var projectId in projectIds)
        {
            yield return ProjectSubjects.For(projectId);
        }
    }

    private static string Format(string name, CultureInfo culture, params object[] values)
    {
        var text = Texts.GetString(name, culture) ?? throw new InvalidOperationException($"The sample text {name} is missing.");
        return string.Format(culture, text, values);
    }
}
