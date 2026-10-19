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
/// projects and on the whole application. The seeder creates both once; afterwards the administration
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
    /// answering to <paramref name="operationsAccountId"/>. Its weekly schedule and the skill that
    /// counts open projects follow with the whole application as a scope.
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

        return await configuration.SaveRunSettingsAsync(
            FollowUpAgentId,
            new NhAssistantAgentRunSettingsRequest(
                NhAssistantAccountabilities.ServiceAccount,
                operationsAccountId.ToString("D"),
                ApprovalPolicy: SampleIntakeReviewerComposition.ApprovalPolicyId,
                MaxRunsPerSubjectPerDay: 2,
                Reason: Reason),
            actorId,
            cancellationToken);
    }
}
