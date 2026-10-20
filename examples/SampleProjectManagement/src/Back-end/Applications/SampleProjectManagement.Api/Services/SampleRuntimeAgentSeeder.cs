using Microsoft.AspNetCore.Identity;
using NewHeap.Platform.AI;
using NewHeap.Platform.AI.Chat;
using NewHeap.Platform.AI.Chat.AspNet.AgentRuns;
using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AspNet.Common.DAL.Entities;
using NewHeap.Platform.Common.Models;
using SampleProjectManagement.Api.Authorization;
using SampleProjectManagement.Api.Composition;
using SampleProjectManagement.Core.Services;

namespace SampleProjectManagement.Api.Services;

/// <summary>
/// Development only: the project starter, a complete agent that works on its own and that an
/// administrator created at runtime rather than in code. It answers to the development operations
/// account and acts with that account's rights, is kept out of the chat, and its kickoff skill
/// starts on every created project: a draft gets a proposal to start it, which the project's approver
/// decides under the intake approval policy. SPM-302: next to it the project follow-up, which works on
/// projects and on the whole application; SPM-304: it joins the project management group. A
/// development database with a follow-up of an earlier version keeps it; put it in the group in the
/// administration, or remove it to seed this one. The seeder creates both once; afterwards the administration
/// owns them, so changes made there survive a restart.
/// </summary>
public sealed class SampleRuntimeAgentSeeder(IServiceScopeFactory scopes, ILogger<SampleRuntimeAgentSeeder> logger) : IHostedService
{
    public const string AgentId = "project-starter";
    public const string KickoffSkill = "kickoff";
    public const string TriggerId = "project-created-kickoff";

    /// <summary>
    /// SPM-302: the project follow-up created as data.
    /// </summary>
    public const string FollowUpAgentId = "project-follow-up";

    /// <summary>
    /// SPM-302: the follow-up's schedule every Monday, without a subject set, so it runs for the whole
    /// application.
    /// </summary>
    public const string FollowUpScheduleId = "project-follow-up-weekly";

    /// <summary>
    /// SPM-302: the follow-up's skill that counts the open projects and proposes to complete the
    /// finished ones, each at its project.
    /// </summary>
    public const string FollowUpSkill = ProjectFollowUpRouting.CompletionSkill;

    private const string Reason = "Development sample of an agent that works on its own";

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var users = scope.ServiceProvider.GetRequiredService<UserManager<NhUser>>();
            var operations = await users.FindByEmailAsync(SampleAuthorizationDefaults.OperationsEmail);
            var administrator = await users.FindByEmailAsync(SampleAuthorizationDefaults.ManagerEmail);
            if (operations is null || administrator is null)
            {
                logger.LogWarning("The project starter was not created: the development accounts are missing.");
                return;
            }

            var result = await SeedAsync(scope.ServiceProvider, operations.Id, administrator.Id, cancellationToken);
            if (!result.Success)
            {
                logger.LogWarning(
                    "The project starter was not created: {Codes}",
                    string.Join(", ", result.GetResultItems().Select(item => item.Name)));
            }

            var followUp = await SeedFollowUpAsync(scope.ServiceProvider, operations.Id, administrator.Id, cancellationToken);
            if (!followUp.Success)
            {
                logger.LogWarning(
                    "The project follow-up was not created: {Codes}",
                    string.Join(", ", followUp.GetResultItems().Select(item => item.Name)));
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // A development convenience never stops the sample from starting.
            logger.LogWarning(exception, "The project starter was not created.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// SPM-298: creates the project starter unless it exists, as an administrator does: the agent,
    /// kept out of the chat; its run settings, answering to <paramref name="operationsAccountId"/>;
    /// the kickoff skill and the trigger on created projects, both put live.
    /// </summary>
    public static async Task<TaskResult> SeedAsync(
        IServiceProvider services,
        Guid operationsAccountId,
        Guid administratorId,
        CancellationToken cancellationToken)
    {
        var configuration = services.GetRequiredService<INhAssistantAgentConfiguration>();
        if (await configuration.GetAsync(AgentId, cancellationToken) is not null)
        {
            return TaskResult.Succeeded();
        }

        var actorId = administratorId.ToString("D");
        var created = await configuration.CreateAgentAsync(
            new NhAssistantAgentRequest(
                AgentId,
                "Project starter",
                "Starts projects that were created as drafts, once the project's approver agrees.",
                "You start projects that were created as drafts. Propose to make a draft project active "
                + "and explain in one sentence why it can start. Never start a project that is not a draft.",
                [ProjectAiTools.ChangeStatusToolId],
                NhAiAutonomyLevel.Propose,
                OfferedInChat: false,
                // SPM-302: it works on projects; its trigger names the project type up front.
                WorksOnSubjectTypes: [ProjectSubjects.Type]),
            actorId,
            cancellationToken);
        if (!created.Success)
        {
            return created;
        }

        var steps = new Func<Task<TaskResult>>[]
        {
            async () => await configuration.SaveRunSettingsAsync(
                AgentId,
                new NhAssistantAgentRunSettingsRequest(
                    NhAssistantAccountabilities.ServiceAccount,
                    operationsAccountId.ToString("D"),
                    ApprovalPolicy: SampleIntakeReviewerComposition.ApprovalPolicyId,
                    MaxRunsPerSubjectPerDay: 2,
                    Reason: Reason),
                actorId,
                cancellationToken),
            async () => await configuration.SaveSkillAsync(
                AgentId,
                KickoffSkill,
                new NhAssistantDataSkillRequest(
                    "Starts a project that was created as a draft.",
                    RuleHandler: SampleProjectStarterComposition.KickoffRules,
                    Tools: [ProjectAiTools.ChangeStatusToolId],
                    Reason: Reason),
                actorId,
                cancellationToken),
            async () => await configuration.SaveTriggerAsync(
                AgentId,
                TriggerId,
                new NhAssistantDataTriggerRequest(
                    NhAssistantDataTriggerSources.Event,
                    KickoffSkill,
                    Event: SampleProjectStarterComposition.ProjectCreatedEvent,
                    Conflict: NhAssistantTriggerConflictNames.Ignore,
                    Reason: Reason),
                actorId,
                cancellationToken),
            async () => await services.GetRequiredService<INhAssistantAgentControl>()
                .SetSkillShadowAsync(AgentId, KickoffSkill, false, actorId, Reason, cancellationToken),
            async () => await configuration.SetTriggerShadowAsync(AgentId, TriggerId, false, actorId, Reason, cancellationToken)
        };
        foreach (var step in steps)
        {
            var result = await step();
            if (!result.Success)
            {
                return result;
            }
        }

        return TaskResult.Succeeded();
    }

    /// <summary>
    /// SPM-302: creates the project follow-up unless it exists, as an administrator does: an agent
    /// kept out of the chat that works on projects and on the whole application, that project managers
    /// may run for the whole application and whose results for it everyone who reads projects sees,
    /// answering to <paramref name="operationsAccountId"/>. Its schedule every Monday names no subject
    /// set, so each tick starts one run for the whole application, in the agent's thread of
    /// <c>nh-system/global</c>, in its <c>complete-projects</c> skill, which proposes to complete the
    /// finished projects, each placed at its project; its routing rules send a run by hand for the whole
    /// application there as well. The seeder puts the skill and the schedule live. A development
    /// database that has the follow-up of an earlier version keeps it; remove the agent in the
    /// administration to seed this one.
    /// </summary>
    public static async Task<TaskResult> SeedFollowUpAsync(
        IServiceProvider services,
        Guid operationsAccountId,
        Guid administratorId,
        CancellationToken cancellationToken)
    {
        var configuration = services.GetRequiredService<INhAssistantAgentConfiguration>();
        if (await configuration.GetAsync(FollowUpAgentId, cancellationToken) is not null)
        {
            return TaskResult.Succeeded();
        }

        var actorId = administratorId.ToString("D");
        var created = await configuration.CreateAgentAsync(
            new NhAssistantAgentRequest(
                FollowUpAgentId,
                "Project follow-up",
                "Keeps the projects moving: counts the open projects and proposes which ones can be completed.",
                "You follow up the projects of the application. Count the open projects; when there are more "
                + "than three, propose which ones can be completed and explain each proposal in one sentence.",
                [ProjectAiTools.ChangeStatusToolId],
                NhAiAutonomyLevel.Propose,
                OfferedInChat: false,
                WorksOnSubjectTypes: [ProjectSubjects.Type],
                WorksOnApplication: true,
                RunPolicy: SampleProjectStarterComposition.ProjectManagePolicy,
                ViewPolicy: SampleProjectStarterComposition.ProjectViewPolicy),
            actorId,
            cancellationToken);
        if (!created.Success)
        {
            return created;
        }

        var steps = new Func<Task<TaskResult>>[]
        {
            // SPM-302: its routing rules send a run for the whole application, also one a project
            // manager starts by hand, to the complete-projects skill.
            async () => await configuration.SaveRunSettingsAsync(
                FollowUpAgentId,
                new NhAssistantAgentRunSettingsRequest(
                    NhAssistantAccountabilities.ServiceAccount,
                    operationsAccountId.ToString("D"),
                    ApprovalPolicy: SampleIntakeReviewerComposition.ApprovalPolicyId,
                    // The weekly tick and a few runs by hand a day for the whole application.
                    MaxRunsPerSubjectPerDay: 10,
                    RuleHandler: SampleProjectStarterComposition.FollowUpRoutingRules,
                    Reason: Reason),
                actorId,
                cancellationToken),
            // SPM-302: the skill counts the open projects and proposes to complete the finished ones;
            // each proposal is placed at its project, where the project's approvers decide.
            async () => await configuration.SaveSkillAsync(
                FollowUpAgentId,
                FollowUpSkill,
                new NhAssistantDataSkillRequest(
                    "Counts the open projects and proposes to complete the finished ones at their projects.",
                    RuleHandler: SampleProjectStarterComposition.FollowUpCompletionRules,
                    Tools: [ProjectAiTools.ChangeStatusToolId],
                    Reason: Reason),
                actorId,
                cancellationToken),
            // Without a subject set the schedule runs for the whole application, which the
            // follow-up works on, in the skill.
            async () => await configuration.SaveTriggerAsync(
                FollowUpAgentId,
                FollowUpScheduleId,
                new NhAssistantDataTriggerRequest(
                    NhAssistantDataTriggerSources.Schedule,
                    FollowUpSkill,
                    Cron: "0 7 * * 1",
                    TimeZone: "Europe/Amsterdam",
                    Message: "It is Monday: count the open projects of the application and propose which ones can be completed.",
                    Reason: Reason),
                actorId,
                cancellationToken),
            async () => await services.GetRequiredService<INhAssistantAgentControl>()
                .SetSkillShadowAsync(FollowUpAgentId, FollowUpSkill, false, actorId, Reason, cancellationToken),
            async () => await configuration.SetTriggerShadowAsync(FollowUpAgentId, FollowUpScheduleId, false, actorId, Reason, cancellationToken),
            // SPM-304: an agent created as data joins groups in the administration: the follow-up
            // joins the project management group, whose board the management portal shows.
            async () => await services.GetRequiredService<INhAssistantAgentGroups>()
                .SetMembershipAsync(FollowUpAgentId, [SampleAgentGroupComposition.ProjectManagement], actorId, cancellationToken)
        };
        foreach (var step in steps)
        {
            var result = await step();
            if (!result.Success)
            {
                return result;
            }
        }

        return TaskResult.Succeeded();
    }
}
