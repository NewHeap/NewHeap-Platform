using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using NewHeap.Platform.AI;
using NewHeap.Platform.AI.Chat;
using SampleProjectManagement.Core.Models.AI;
using SampleProjectManagement.Core.Services;
using SampleProjectManagement.DAL;
using SampleProjectManagement.DAL.Entities;

namespace SampleProjectManagement.Api.Composition;

/// <summary>
/// SPM-302: the routing rules of the project follow-up, an agent created as data. A run for the whole
/// application, a weekly tick or a project manager's run now, works in the follow-up's
/// <c>complete-projects</c> skill; a run on one project goes to the agent's model, which answers people
/// in the project's thread. The rules call no model.
/// </summary>
public sealed class ProjectFollowUpRouting(IStringLocalizer<SampleAssistantToolPresenter> localizer) : INhAssistantAgentRuleHandler
{
    /// <summary>The skill that counts the open projects and proposes to complete the finished ones.</summary>
    public const string CompletionSkill = "complete-projects";

    public Task<NhAssistantRuleResult> RunAsync(NhAssistantRuleContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        return Task.FromResult(NhAiSystemSubjects.IsSystem(context.Subject.Subject)
            ? NhAssistantRuleResult.UseSkill(CompletionSkill, localizer["FollowUpWholeApplication"].Value)
            : NhAssistantRuleResult.HandOver(localizer["FollowUpOnProject"].Value));
    }
}

/// <summary>
/// SPM-302: the <c>complete-projects</c> skill of the project follow-up, by fixed rules. It counts the
/// open projects of the whole application and, when there are more than three, proposes to complete
/// every active project whose tasks are all completed, the most recently changed first. The tool's
/// input names the project with <c>[NhAiSubject("project")]</c>, so the platform places each
/// proposal in the follow-up's thread at that project, after checking that the follow-up works on
/// projects and that the operations account it answers to reads the project; the project's approvers
/// decide it there and in their inbox. A run places at most twenty proposals. SPM-304: what the rules
/// write ("N open projects; proposed completing X, Y") is the run's result, which the runs view and
/// <c>GET runs</c> show to the people who pass the follow-up's view policy. The rules write it per
/// culture (<c>NhAssistantRuleResult.Done(culture =&gt; ...)</c>), so an English reader reads it in
/// English and a Dutch reader in Dutch, whatever culture the run worked in.
/// </summary>
public sealed class ProjectFollowUpRules(
    SampleProjectManagementDbContext dbContext,
    IStringLocalizer<SampleAssistantToolPresenter> localizer) : INhAssistantAgentRuleHandler
{
    /// <summary>With more open projects than this the follow-up proposes completions.</summary>
    public const int OpenThreshold = 3;

    /// <summary>The most completions one run proposes, the platform's default placing limit.</summary>
    public const int MaxProposals = 20;

    public async Task<NhAssistantRuleResult> RunAsync(NhAssistantRuleContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var open = await dbContext.Projects
            .AsNoTracking()
            .CountAsync(
                project => project.Status == ProjectStatus.Draft
                    || project.Status == ProjectStatus.Active
                    || project.Status == ProjectStatus.OnHold,
                cancellationToken);
        if (open <= OpenThreshold)
        {
            // The platform asks once per supported culture, with that culture current.
            return NhAssistantRuleResult.Done(_ => localizer["FollowUpFewOpen", open].Value);
        }

        // The same readiness as the project closer checks (SPM-283): active, with tasks, none open.
        var finished = await dbContext.Projects
            .AsNoTracking()
            .Where(project => project.Status == ProjectStatus.Active
                && project.Tasks.Any()
                && project.Tasks.All(task => task.IsCompleted))
            .OrderByDescending(project => project.LastModifiedDateTime)
            .ThenBy(project => project.Id)
            .Select(project => new { project.Id, project.Key, Tasks = project.Tasks.Count })
            .Take(MaxProposals)
            .ToListAsync(cancellationToken);

        var proposed = new List<string>();
        var waiting = 0;
        var refused = 0;
        foreach (var project in finished)
        {
            var proposal = await context.ProposeAsync(
                ProjectAiTools.ChangeStatusToolId,
                new ProjectAiStatusChangeInput(project.Id, ProjectStatus.Completed),
                // The platform asks once per supported culture, with that culture current.
                _ => localizer["FollowUpReason", project.Tasks, open].Value,
                cancellationToken);
            switch (proposal.Outcome)
            {
                case NhAssistantProposeOutcome.Created:
                    proposed.Add(project.Key);
                    break;
                case NhAssistantProposeOutcome.AlreadyOpen:
                case NhAssistantProposeOutcome.RecentlyRejected:
                    waiting++;
                    break;
                default:
                    // Not placed, for example because the account the follow-up answers to does not
                    // read the project: the reason is in the run's thread and the audit.
                    refused++;
                    break;
            }
        }

        // The run's result names the projects it proposed to complete; people read it in the runs view,
        // on the board and with the project's agents, each in their own language.
        return NhAssistantRuleResult.Done(_ => proposed.Count > 0
            ? localizer["FollowUpProposed", open, string.Join(", ", proposed), waiting, refused].Value
            : localizer["FollowUpNothingNew", open, waiting, refused].Value);
    }
}
