using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using NewHeap.Platform.AI.Chat;
using NewHeap.Platform.AI.Chat.AspNet;
using SampleProjectManagement.Core.Events;
using SampleProjectManagement.Core.Models.AI;
using SampleProjectManagement.Core.Services;
using SampleProjectManagement.DAL;
using SampleProjectManagement.DAL.Entities;

namespace SampleProjectManagement.Api.Composition;

/// <summary>
/// SPM-296: the building blocks of the project starter, an agent an administrator creates in the
/// administration rather than in code. The composition registers what its run settings and its
/// trigger name: the project-created event and the kickoff rules. The agent itself, whom it answers
/// to and when it starts are data; it has no grants and acts with the rights of the technical account
/// it answers to. SPM-302: it also lists the policies an administrator may choose as the run and view
/// policy of such an agent, and lets everyone who reads the projects read the whole application: the
/// assistant's access policy needs an active division, which the whole application app-wide has
/// not. Call it after <c>AddSampleIntakeReviewer</c>, which registers the project subject type.
/// </summary>
public static class SampleProjectStarterComposition
{
    /// <summary>
    /// The names data refers to.
    /// </summary>
    public const string ProjectCreatedEvent = "project.created";
    public const string KickoffRules = "project-kickoff";

    /// <summary>
    /// SPM-302: who may run an agent created here for the whole application, and who sees its results.
    /// </summary>
    public const string ProjectManagePolicy = "app.project.manage";
    public const string ProjectViewPolicy = "app.project.view";

    public static IServiceCollection AddSampleProjectStarterBlocks(this IServiceCollection services)
    {
        services.AddNewHeapAssistant(assistant => assistant
            // SPM-302: the agent editor offers these as run and view policies; startup checks that
            // the policies are registered.
            .AddAgentPolicy(ProjectManagePolicy, "Project managers: people who may change projects.")
            .AddAgentPolicy(ProjectViewPolicy, "Everyone who may read the projects.")
            // SPM-302: the whole application is read app-wide; administrators decide its proposals,
            // as the default approve policy (the administration policy) says.
            .UseSystemSubject(system => system.ReadPolicy(ProjectViewPolicy))
            .AddTriggerEvent<ProjectCreatedEvent>(ProjectCreatedEvent, evt => evt
                .Description("A project was created.")
                .Subject(ProjectSubjects.Type, created => ProjectSubjects.IdOf(created.ProjectId))
                .Initiator(created => created.CreatedByUserId?.ToString("D"))
                .Message(created => $"Project {created.ProjectKey} was created.")
                .Dedupe(created => created.EventId.ToString("N")))
            .AddRuleHandler<ProjectKickoffRules>(KickoffRules, "Proposes to start a project that was created as a draft, without a model."));
        return services;
    }
}

/// <summary>
/// SPM-296: the rules of the project starter. A project created as a draft gets a proposal to start
/// it; the proposal goes through the agent's approval policy, with the rights of the account the
/// agent answers to. They call no model.
/// </summary>
public sealed class ProjectKickoffRules(
    SampleProjectManagementDbContext dbContext,
    IStringLocalizer<SampleAssistantToolPresenter> localizer) : INhAssistantAgentRuleHandler
{
    public async Task<NhAssistantRuleResult> RunAsync(NhAssistantRuleContext context, CancellationToken cancellationToken)
    {
        var projectId = Guid.Parse(context.Subject.Subject.Id);
        var status = await dbContext.Projects
            .AsNoTracking()
            .Where(project => project.Id == projectId)
            .Select(project => (ProjectStatus?)project.Status)
            .FirstOrDefaultAsync(cancellationToken);
        if (status != ProjectStatus.Draft)
        {
            return NhAssistantRuleResult.Done(localizer["StarterNotDraft"].Value);
        }

        var proposal = await context.ProposeAsync(
            ProjectAiTools.ChangeStatusToolId,
            new ProjectAiStatusChangeInput(projectId, ProjectStatus.Active),
            // The platform asks once per supported culture, with that culture current.
            _ => localizer["StarterReason"].Value,
            cancellationToken);
        return NhAssistantRuleResult.Done(proposal.Outcome switch
        {
            NhAssistantProposeOutcome.Created => localizer["StarterProposed"].Value,
            NhAssistantProposeOutcome.AlreadyOpen => localizer["StarterAlreadyOpen"].Value,
            _ => localizer["StarterNotProposed", proposal.Code ?? proposal.Outcome.ToString()].Value
        });
    }
}
