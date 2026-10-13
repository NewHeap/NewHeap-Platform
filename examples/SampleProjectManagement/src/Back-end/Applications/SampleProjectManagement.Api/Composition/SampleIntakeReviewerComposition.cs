using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NewHeap.Platform.AI;
using NewHeap.Platform.AI.Chat;
using NewHeap.Platform.AI.Chat.AspNet;
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
/// (<c>projects.request-review</c>), a call that may run only once.
/// Call it after <c>AddSampleAssistant</c>; the host must call <c>AddAssistantAgentRuns</c> inside
/// <c>WithBackgroundOperations</c>.
/// </summary>
public static class SampleIntakeReviewerComposition
{
    public const string AgentId = "sample-project-intake-reviewer";
    public const string TriggerId = "project-created-intake";
    public const string ApprovalPolicyId = "project-intake";
    public const string ReviewBoardTriggerId = "review-board-feedback";
    public const string DeadlineReviewTriggerId = "weekly-deadline-review";

    /// <summary>
    /// The configuration key of the secret the review board signs its webhook calls with.
    /// </summary>
    public const string ReviewBoardSecretKey = "SampleProjectManagement:ReviewBoard:WebhookSecret";

    public static IServiceCollection AddSampleIntakeReviewer(this IServiceCollection services)
    {
        // The review request runs only for agents and approved proposals of this composition.
        services.TryAddScoped<ProjectAiReviewTools>();
        services.AddNewHeapPlatformAI(ai => ai.AddGeneratedToolCatalog<ProjectAiReviewToolsNhAiCatalog>());

        // A second AddNewHeapAssistant call extends the assistant registered by AddSampleAssistant.
        services.AddNewHeapAssistant(assistant => assistant
            .AddAgent(new NhAssistantAgentDefinition(
                Id: AgentId,
                Version: 1,
                DisplayNameKey: "nh-assistant.agents.sample-project-intake-reviewer.name",
                DescriptionKey: "nh-assistant.agents.sample-project-intake-reviewer.description",
                ProfileName: SampleAssistantComposition.ProfileName,
                Instructions: ProjectAiAssets.ProjectIntakeReviewerInstructions,
                ToolSelectors: [ProjectAiTools.ChangeStatusToolId, ProjectAiReviewTools.RequestReviewToolId, ProjectAiReviewTools.DraftIntakeNoteToolId],
                Autonomy: NhAiAutonomyLevel.Execute,
                RequiredPolicy: SampleAssistantComposition.AccessPolicy))
            // Project members see the project's agent threads; their project role decides whether
            // they read, take part or decide proposals.
            .AddSubjectType<ProjectSubjectAccessResolver>()
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
            .ConfigureAgent(AgentId, agent => agent
                .Grant(ProjectAiTools.ChangeStatusToolId, ProjectAiReviewTools.RequestReviewToolId, ProjectAiReviewTools.DraftIntakeNoteToolId)
                .TriggerOnly()
                // SPM-276: what the reviewer checked survives between runs as structured memory.
                .Memory<ProjectIntakeMemory>(schemaVersion: 1)
                .UseApprovalPolicy(ApprovalPolicyId)
                .Retry(2)
                .Budget(
                    NhAssistantAgentBudget.PerDay(toolCalls: 50, tokens: 200_000),
                    maxConcurrentRuns: 2,
                    maxRunsPerSubjectPerDay: 5)
                // SPM-278: the project assistant may consult the reviewer about a project. A
                // consultation uses the asking person's rights, never the grants above; project
                // readers get an answer, participants may also leave proposals for the approvers.
                // Consultations spend the same daily budget. MCP clients would need Exposure(... | Mcp).
                .AllowConsultation(ProjectSubjects.Type, consultation => consultation
                    .Description("Knows the intake review of a project: the checks done, the last verdict and what is still missing.")
                    .Policy(SampleAssistantComposition.AccessPolicy)
                    .Instructions(ProjectAiAssets.ProjectIntakeReviewerConsultationInstructions)
                    .Limits(maxToolCalls: 2, timeout: TimeSpan.FromSeconds(45), maxAnswerChars: 2_000)
                    // Participants may also ask the reviewer to review the project again in a run
                    // of its own; they are notified when it ends.
                    .AllowRequests()))
            .AddTrigger<ProjectCreatedEvent>(TriggerId, trigger => trigger
                .StartAgent(AgentId)
                .Subject(evt => ProjectSubjects.For(evt.ProjectId))
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
                .Secret(provider => provider.GetRequiredService<IConfiguration>()[ReviewBoardSecretKey])
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
