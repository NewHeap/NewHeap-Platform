using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NewHeap.Platform.AI;
using NewHeap.Platform.AI.Chat;
using NewHeap.Platform.AI.Chat.AspNet;
using NewHeap.Platform.Common;
using SampleProjectManagement.Core.Events;
using SampleProjectManagement.Core.Services;
using SampleProjectManagement.DAL;
using SampleProjectManagement.DAL.Entities;

namespace SampleProjectManagement.Api.Composition;

/// <summary>
/// Composes the intake reviewer, an agent that starts on its own when a project is created. It
/// answers for its runs itself, may propose project status changes that the person it works for
/// could not make (the tool opts in with <c>AllowAgentGrant</c>), and never executes them: every
/// proposal waits in the project's thread until a project approver other than the project's
/// creator decides it. Approved proposals execute as background operations. A participant can
/// also start it on a project with <c>POST threads/start</c>, for example to request a review
/// (<c>projects.request-review</c>), a call that may run only once, or to tick off a task
/// (<c>project-tasks.complete</c>). Proposals that people overtake by hand close on their own
/// (SPM-282): a task completed by hand closes the proposal to complete it, and a status set by hand
/// closes the proposal to set it.
/// Call it after <c>AddSampleAssistant</c>; the host must call <c>AddAssistantAgentRuns</c> inside
/// <c>WithBackgroundOperations</c>.
/// </summary>
public static class SampleIntakeReviewerComposition
{
    public const string AgentId = "sample-project-intake-reviewer";
    public const string TriggerId = "project-created-intake";
    public const string ApprovalPolicyId = "project-intake";

    /// <summary>
    /// SPM-278: the policy of what the reviewer proposes while the project assistant consults it: the
    /// person who asked decides it in their own chat.
    /// </summary>
    public const string ConsultationPolicyId = "project-intake-consultation";
    public const string ReviewBoardTriggerId = "review-board-feedback";
    public const string DeadlineReviewTriggerId = "weekly-deadline-review";

    /// <summary>
    /// The configuration key of the secret the review board signs its webhook calls with, set in the
    /// sample's <c>secrets.json</c> and read through <see cref="NhConfigurationSecrets"/>.
    /// </summary>
    public const string ReviewBoardSecretKey = "SampleProjectManagement:ReviewBoard:WebhookSecret";

    public static IServiceCollection AddSampleIntakeReviewer(this IServiceCollection services)
    {
        // The review request runs only for agents and approved proposals of this composition.
        services.TryAddScoped<ProjectAiReviewTools>();
        services.TryAddScoped<ProjectTaskAiTools>();
        services.AddNewHeapPlatformAI(ai => ai
            .AddGeneratedToolCatalog<ProjectAiReviewToolsNhAiCatalog>()
            .AddGeneratedToolCatalog<ProjectTaskAiToolsNhAiCatalog>());

        // A second AddNewHeapAssistant call extends the assistant registered by AddSampleAssistant.
        services.AddNewHeapAssistant(assistant => assistant
            .AddAgent(new NhAssistantAgentDefinition(
                Id: AgentId,
                Version: 1,
                DisplayNameKey: "nh-assistant.agents.sample-project-intake-reviewer.name",
                DescriptionKey: "nh-assistant.agents.sample-project-intake-reviewer.description",
                ProfileName: SampleAssistantComposition.ProfileName,
                Instructions: ProjectAiAssets.ProjectIntakeReviewerInstructions,
                ToolSelectors:
                [
                    ProjectAiTools.ChangeStatusToolId,
                    ProjectAiReviewTools.RequestReviewToolId,
                    ProjectAiReviewTools.DraftIntakeNoteToolId,
                    ProjectTaskAiTools.CompleteToolId,
                    // SPM-314: granted and executed by the application in SampleEquipmentOrderComposition.
                    ProjectAiEquipmentTools.OrderEquipmentToolId
                ],
                Autonomy: NhAiAutonomyLevel.Execute,
                RequiredPolicy: SampleAssistantComposition.AccessPolicy))
            // Project members see the project's agent threads; their project role decides whether
            // they read, take part or decide proposals. SPM-301: people read the type by its name in
            // English or Dutch, find a project by key or name (the resolver also searches) and open
            // it on the page where the portal shows a project's agent work.
            .AddSubjectType<ProjectSubjectAccessResolver>(type => type
                .Named(SampleSubjectTypeNames.Of("SubjectTypeProject"), fallbackLanguage: "en")
                .NamedPlural(SampleSubjectTypeNames.Of("SubjectTypeProjects"))
                .ForEntity<Project>()
                .OpenAt(ProjectSubjects.LinkTemplate))
            // SPM-285: every model turn about a project, of any agent, reads its briefing first.
            .AddSubjectBriefing<ProjectBriefingProvider>()
            .AddApprovalPolicy(ApprovalPolicyId, policy => policy
                .Mode(NhAiApprovalMode.Deferred)
                .Lifetime(TimeSpan.FromDays(3))
                .Approvers(NhAiApprovers.SubjectRole(NhAiSubjectRole.Approver))
                // The person who created the project starts the review and cannot approve it.
                .FourEyes()
                .Reminders(TimeSpan.FromDays(1))
                .OnExpiry(NhAiApprovalExpiryAction.Withdraw)
                // An edit after the proposal closes it as stale instead of executing it.
                .RequireUnchangedSubject()
                .BindTo(NhAiApprovalBinding.ToolContract))
            // SPM-278: what the reviewer proposes in a consultation is the asker's to decide, right
            // away in their own chat. Its own policy above is four-eyes and keeps the asker out, so
            // the consultation names this one; a policy for one tool would still win.
            .AddApprovalPolicy(ConsultationPolicyId, policy => policy
                .Approvers(NhAiApprovers.Initiator)
                .Lifetime(TimeSpan.FromMinutes(15))
                .BindTo(NhAiApprovalBinding.ToolContract))
            // SPM-279: approvers may say why they rejected a proposal. The next run on the project
            // starts from those rejections, administrators read the report, and this sink forwards
            // each feedback to the application's own analytics.
            .AddProposalFeedbackSink<SampleProposalFeedbackLog>()
            // SPM-282: proposals that people overtake by hand close on their own. The guards say
            // when a proposal no longer fits and why; they run when the application revalidates,
            // when an approver decides and right before execution. The task guard names the task a
            // proposal changes, so an update of that task finds the proposal by key. It also checks
            // when the reviewer proposes, so a task someone already ticked off is never proposed.
            .AddProposalGuard<ProjectTaskProposalGuard>(
                [ProjectTaskAiTools.CompleteToolId],
                NhAiProposalGuardStages.All | NhAiProposalGuardStages.Proposal)
            .AddProposalGuard<ProjectStatusProposalGuard>(ProjectAiTools.ChangeStatusToolId)
            .AddProposalRevalidation<ProjectTaskUpdatedEvent>(revalidation => revalidation
                .Keys(evt => [ProjectTaskProposalGuard.Key(evt.ProjectTaskId)])
                .ScopeKey(evt => NhAiSubjectInfo.CreateScopeKey(null, evt.DivisionId)))
            .ConfigureAgent(AgentId, agent => agent
                // SPM-302: the reviewer works on projects only; triggers, starts and consultations
                // about another type are refused, and startup checks the typed trigger below.
                .WorksOn(ProjectSubjects.Type)
                .Grant(
                    ProjectAiTools.ChangeStatusToolId,
                    ProjectAiReviewTools.RequestReviewToolId,
                    ProjectAiReviewTools.DraftIntakeNoteToolId,
                    ProjectTaskAiTools.CompleteToolId)
                .TriggerOnly()
                // SPM-276: what the reviewer checked survives between runs as structured memory.
                .Memory<ProjectIntakeMemory>(schemaVersion: 1)
                .UseApprovalPolicy(ApprovalPolicyId)
                .Retry(2)
                .Budget(
                    NhAssistantAgentBudget.PerDay(toolCalls: 50, tokens: 200_000),
                    maxConcurrentRuns: 2,
                    maxRunsPerSubjectPerDay: 5)
                // SPM-285: one run never spends more than this; a run that keeps calling tools stops
                // before its next model call and an approver can resume it.
                .MaxTokensPerRun(60_000)
                // SPM-278: the project assistant may consult the reviewer about a project. A
                // consultation uses the asking person's rights, never the grants above; project
                // readers get an answer, participants may also propose. A participant decides what
                // the reviewer proposes in their own chat, and it executes in their own request with
                // their own rights, so no person's principal is rebuilt (AskerDecides). Consultations
                // spend the same daily budget. MCP clients would need Exposure(... | Mcp).
                .AllowConsultation(ProjectSubjects.Type, consultation => consultation
                    .Description("Knows the intake review of a project: the checks done, the last verdict and what is still missing.")
                    .Policy(SampleAssistantComposition.AccessPolicy)
                    .Instructions(ProjectAiAssets.ProjectIntakeReviewerConsultationInstructions)
                    // Asked about a project's intake, the reviewer may only request a review: its
                    // status and task tools stay with its own runs. The consulted model is not offered
                    // them, and a call of one runs nothing. Each tool is one of the agent's own.
                    .Tools(ProjectAiReviewTools.RequestReviewToolId)
                    .Limits(maxToolCalls: 2, timeout: TimeSpan.FromSeconds(45), maxAnswerChars: 2_000)
                    .Proposals(proposals => proposals
                        .MinimumRole(NhAiSubjectRole.Participant)
                        .AskerDecides()
                        .Policy(ConsultationPolicyId))
                    // Participants may also ask the reviewer to review the project again in a run
                    // of its own; they are notified when it ends.
                    .AllowRequests()))
            .AddTrigger<ProjectCreatedEvent>(TriggerId, trigger => trigger
                .StartAgent(AgentId)
                .Subject(ProjectSubjects.Type, evt => ProjectSubjects.IdOf(evt.ProjectId))
                .Initiator(evt => evt.CreatedByUserId?.ToString("D"))
                .Message(evt =>
                    $"New project {evt.ProjectKey} ({evt.ProjectId:D}) needs an intake review. Propose status active when it is ready.")
                // The outbox delivers an event at least once; the event id starts one run.
                .Dedupe(evt => evt.EventId.ToString("N"))
                .OnBusy(NhAssistantTriggerConflict.Coalesce, TimeSpan.Zero))
            // SPM-275: the review board answers a review request through a signed webhook call.
            // The body is untrusted: only a project id and a known verdict start the agent.
            .AddWebhookTrigger(ReviewBoardTriggerId, webhook => webhook
                .StartAgent(AgentId)
                .Secret(provider => NhConfigurationSecrets.Read(provider.GetRequiredService<IConfiguration>(), ReviewBoardSecretKey))
                .Subject(body => ReviewBoardProject(body) is { } projectId ? ProjectSubjects.For(projectId) : null)
                .Message(body =>
                    $"The review board answered for project {ReviewBoardProject(body):D}: propose status {(ReviewBoardVerdict(body) == "approved" ? "active" : "onhold")}.")
                .OnBusy(NhAssistantTriggerConflict.Coalesce, TimeSpan.Zero))
            // SPM-275: every Monday at seven the reviewer looks at active projects whose deadline
            // falls within a week; a project with an active run skips the tick.
            .AddScheduledTrigger(DeadlineReviewTriggerId, schedule => schedule
                .StartAgent(AgentId)
                .Cron("0 7 * * 1", TimeZoneInfo.FindSystemTimeZoneById("Europe/Amsterdam"))
                .Subjects(ProjectsWithDeadlineThisWeek)
                .Message(tick => $"Weekly review: project {tick.Subject.Id} has its deadline within a week. Propose status onhold when it is behind.")
                .MaxSubjectsPerTick(50)
                .OnBusy(NhAssistantTriggerConflict.Ignore)));
        return services;
    }

    private static Guid? ReviewBoardProject(JsonElement body)
    {
        return body.ValueKind == JsonValueKind.Object
               && body.TryGetProperty("projectId", out var value)
               && value.ValueKind == JsonValueKind.String
               && Guid.TryParse(value.GetString(), out var projectId)
            ? projectId
            : null;
    }

    private static string? ReviewBoardVerdict(JsonElement body)
    {
        return body.TryGetProperty("verdict", out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() switch
            {
                "approved" => "approved",
                "rejected" => "rejected",
                _ => null
            }
            : null;
    }

    private static async IAsyncEnumerable<NhAiSubjectRef> ProjectsWithDeadlineThisWeek(
        IServiceProvider services,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var dbContext = services.GetRequiredService<SampleProjectManagementDbContext>();
        var projectIds = await dbContext.Projects
            .AsNoTracking()
            .Where(project => project.Status == ProjectStatus.Active
                && project.Deadline != null
                && project.Deadline >= now
                && project.Deadline <= now.AddDays(7))
            .OrderBy(project => project.Deadline)
            .Select(project => project.Id)
            .Take(50)
            .ToListAsync(cancellationToken);
        foreach (var projectId in projectIds)
        {
            yield return ProjectSubjects.For(projectId);
        }
    }
}

/// <summary>
/// SPM-301: the names of the sample's subject types per language, from the same resources as the
/// tool presentations, so every language's text stays with the application's translations.
/// </summary>
internal static class SampleSubjectTypeNames
{
    private static readonly System.Resources.ResourceManager Resources = new(typeof(SampleAssistantToolPresenter));

    public static Dictionary<string, string> Of(string resourceName)
    {
        return new Dictionary<string, string>
        {
            ["en"] = Text(resourceName, "en-US"),
            ["nl"] = Text(resourceName, "nl-NL")
        };
    }

    private static string Text(string resourceName, string culture)
    {
        return Resources.GetString(resourceName, System.Globalization.CultureInfo.GetCultureInfo(culture))
            ?? throw new InvalidOperationException($"The resource '{resourceName}' has no text for {culture}.");
    }
}
