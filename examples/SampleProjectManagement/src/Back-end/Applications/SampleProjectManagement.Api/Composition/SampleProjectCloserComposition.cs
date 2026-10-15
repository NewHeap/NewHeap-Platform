using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using NewHeap.Platform.AI;
using NewHeap.Platform.AI.Chat;
using NewHeap.Platform.AI.Chat.AspNet;
using SampleProjectManagement.Core.Events;
using SampleProjectManagement.Core.Models.AI;
using SampleProjectManagement.Core.Services;
using SampleProjectManagement.DAL;
using SampleProjectManagement.DAL.Entities;

namespace SampleProjectManagement.Api.Composition;

/// <summary>
/// SPM-283: composes the project closer, an agent whose runs follow fixed rules instead of a
/// language model. When a task of an active project is completed and no task is left open, its
/// rules propose to complete the project; a project approver decides. The rules call no model and
/// spend no tokens; the same proposal is never recorded twice, and a rejected proposal stays quiet
/// for the policy's cooldown. People who ask the closer about a proposal in the project's thread
/// get an answer from its model, which reads the reasons the rules wrote.
/// Call it after <c>AddSampleIntakeReviewer</c>, which registers the project subject type; the host
/// must call <c>AddAssistantAgentRuns</c> inside <c>WithBackgroundOperations</c>.
/// </summary>
public static class SampleProjectCloserComposition
{
    public const string AgentId = "sample-project-closer";
    public const string TriggerId = "project-task-completed-closer";
    public const string ApprovalPolicyId = "project-closing";

    public static IServiceCollection AddSampleProjectCloser(this IServiceCollection services)
    {
        services.AddNewHeapAssistant(assistant => assistant
            .AddAgent(new NhAssistantAgentDefinition(
                Id: AgentId,
                Version: 1,
                DisplayNameKey: "nh-assistant.agents.sample-project-closer.name",
                DescriptionKey: "nh-assistant.agents.sample-project-closer.description",
                // The model answers people in the thread; the rules run every headless run.
                ProfileName: SampleAssistantComposition.ProfileName,
                Instructions: ProjectAiAssets.ProjectCloserInstructions,
                ToolSelectors: [ProjectAiTools.ChangeStatusToolId],
                Autonomy: NhAiAutonomyLevel.Execute,
                RequiredPolicy: SampleAssistantComposition.AccessPolicy))
            .AddApprovalPolicy(ApprovalPolicyId, policy => policy
                .Mode(NhAiApprovalMode.Deferred)
                .Lifetime(TimeSpan.FromDays(7))
                .Approvers(NhAiApprovers.SubjectRole(NhAiSubjectRole.Approver))
                .OnExpiry(NhAiApprovalExpiryAction.Withdraw)
                // An identical proposal is never recorded twice. A newer proposal for the same
                // objects replaces an open one, for tools whose guard names the objects (as the task
                // guard does); the status guard names none, so the closer relies on identity.
                .Duplicates(NhAiDuplicateProposals.Supersede)
                // After an approver rejects completing the project, the rules do not propose it
                // again for two weeks, however many tasks change.
                .RejectionCooldown(TimeSpan.FromDays(14)))
            .ConfigureAgent(AgentId, agent => agent
                // Headless runs go to the rules; a person's question in the thread goes to the model.
                .UseRuleHandler<ProjectCloserRules>()
                .Grant(ProjectAiTools.ChangeStatusToolId)
                .TriggerOnly()
                .UseApprovalPolicy(ApprovalPolicyId)
                .Retry(2)
                .Budget(
                    NhAssistantAgentBudget.PerDay(toolCalls: 50, tokens: 50_000),
                    maxConcurrentRuns: 2,
                    maxRunsPerSubjectPerDay: 20))
            .AddTrigger<ProjectTaskUpdatedEvent>(TriggerId, trigger => trigger
                .StartAgent(AgentId)
                .Subject(evt => ProjectSubjects.For(evt.ProjectId))
                .Message(evt => evt.IsCompleted
                    ? $"Task {evt.ProjectTaskId:D} of project {evt.ProjectId:D} was completed."
                    : $"Task {evt.ProjectTaskId:D} of project {evt.ProjectId:D} changed.")
                // Runs keep no typed event; the rules read this small payload from their inputs.
                .Payload(evt => new ProjectTaskChange(evt.ProjectTaskId, evt.IsCompleted))
                .Dedupe(evt => evt.EventId.ToString("N"))
                .OnBusy(NhAssistantTriggerConflict.Coalesce, TimeSpan.Zero)));
        return services;
    }
}

/// <summary>
/// What a task update tells the project closer's rules.
/// </summary>
public sealed record ProjectTaskChange(Guid TaskId, bool IsCompleted);

/// <summary>
/// SPM-283: the project closer's rules. They read the project as it is now, because events
/// coalesce, arrive late and repeat, and propose to complete an active project once all its tasks
/// are completed. The summary and the reason are the application's own texts in each language.
/// </summary>
public sealed class ProjectCloserRules(
    SampleProjectManagementDbContext dbContext,
    IStringLocalizer<SampleAssistantToolPresenter> localizer) : INhAssistantAgentRuleHandler
{
    public async Task<NhAssistantRuleResult> RunAsync(NhAssistantRuleContext context, CancellationToken cancellationToken)
    {
        if (!context.Inputs.Any(input => input.GetPayload<ProjectTaskChange>()?.IsCompleted == true))
        {
            return NhAssistantRuleResult.Done(localizer["CloserNoTaskCompleted"].Value);
        }

        // The same check as the project briefing reports to the model (SPM-285).
        var check = await ProjectCompletionCheck.ReadAsync(dbContext, Guid.Parse(context.Subject.Subject.Id), cancellationToken);
        if (check is not { IsActive: true })
        {
            return NhAssistantRuleResult.Done(localizer["CloserProjectNotActive"].Value);
        }
        if (!check.ReadyToComplete)
        {
            return NhAssistantRuleResult.Done(localizer["CloserTasksOpen", check.Open, check.Total].Value);
        }

        var projectId = Guid.Parse(context.Subject.Subject.Id);
        var proposal = await context.ProposeAsync(
            ProjectAiTools.ChangeStatusToolId,
            new ProjectAiStatusChangeInput(projectId, ProjectStatus.Completed),
            // The platform asks once per supported culture, with that culture current.
            _ => localizer["CloserReason", check.Total].Value,
            cancellationToken);
        return NhAssistantRuleResult.Done(proposal.Outcome switch
        {
            NhAssistantProposeOutcome.Created => localizer["CloserProposed"].Value,
            NhAssistantProposeOutcome.AlreadyOpen => localizer["CloserAlreadyOpen"].Value,
            NhAssistantProposeOutcome.RecentlyRejected => localizer[
                "CloserRecentlyRejected",
                proposal.RejectedAt?.ToString("d", CultureInfo.CurrentCulture) ?? string.Empty].Value,
            _ => localizer["CloserRefused"].Value
        });
    }
}
