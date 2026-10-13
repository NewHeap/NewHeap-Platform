using System.Security.Claims;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NewHeap.Platform.AI.AspNet;
using NewHeap.Platform.AI.Chat.AspNet.AgentRuns;
using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.AI.Chat.Runs;
using NewHeap.Platform.AI.Chat.Runtime;
using NewHeap.Platform.AI.Chat.Tests.Infrastructure;
using NewHeap.Platform.AI.Test;
using Xunit;

namespace NewHeap.Platform.AI.Chat.Tests;

/// <summary>
/// Agent work on the application's own subjects through <see cref="INhAssistantSubjectWork"/>:
/// everything on one subject with live decision rights, and counts for many subjects in one query,
/// on both relational providers.
/// </summary>
[Collection(AssistantDatabaseCollection.Name)]
public sealed class AssistantSubjectWorkTests(AssistantDatabaseFixture database)
{
    private const string AgentId = "project-assistant";
    private const string ChangeStatusFunction = "projects_change_status_v1";
    private const string Approver = "approver-1";
    private const string Member = "member-1";

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_subject_shows_its_agents_and_proposals_and_a_list_counts_them_in_one_call(AssistantTestProvider provider)
    {
        var busyProject = Guid.NewGuid();
        var quietProject = Guid.NewGuid();
        var model = new NhAiScriptedChatClient()
            .RespondWithFunctionCall(ChangeStatusFunction, new { input = new { projectId = busyProject, status = "Active" } }, "call-status")
            .RespondWithText("I proposed to activate the project.");
        await using var host = await CreateHostAsync(provider, model);
        await StartAndRunAsync(host, busyProject);
        await using (var scope = host.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<INhAiSubjectAccess>().RefreshAsync(Subject(busyProject));
        }

        var approverView = await GetAsync(host, busyProject, Approver);
        Assert.True(approverView.Success);
        Assert.Equal("approver", approverView.Data!.Role);
        var agent = Assert.Single(approverView.Data.Agents);
        Assert.Equal(AgentId, agent.AgentId);
        Assert.NotNull(agent.Thread);
        Assert.True(agent.CanStart);
        var proposal = Assert.Single(approverView.Data.OpenProposals);
        Assert.True(proposal.CanDecide);
        Assert.Null(proposal.ReasonCode);

        // The participant sees the same proposal, but the policy leaves the decision to approvers.
        var memberView = (await GetAsync(host, busyProject, Member)).Data!;
        var memberProposal = Assert.Single(memberView.OpenProposals);
        Assert.False(memberProposal.CanDecide);
        Assert.Equal(NhAssistantErrorCodes.ApprovalPolicyDenied, memberProposal.ReasonCode);
        Assert.Equal(NhAssistantErrorCodes.SubjectNotFound, Code(await GetAsync(host, busyProject, "stranger")));

        // A subject the agent never worked on shows the requested agent without a thread.
        var quiet = (await GetAsync(host, quietProject, Approver, AgentId)).Data!;
        var quietAgent = Assert.Single(quiet.Agents);
        Assert.Null(quietAgent.Thread);
        Assert.Empty(quiet.OpenProposals);

        // A list counts all subjects in one call, in request order.
        var ids = new[] { quietProject.ToString(), busyProject.ToString() };
        var approverSummary = (await SummarizeAsync(host, ids, Approver)).Data!;
        Assert.Equal(ids, approverSummary.Items.Select(item => item.SubjectId));
        Assert.Equal(new NhAssistantSubjectSummaryDto(quietProject.ToString(), 0, 0, false), approverSummary.Items[0]);
        Assert.Equal(new NhAssistantSubjectSummaryDto(busyProject.ToString(), 1, 1, true), approverSummary.Items[1]);
        Assert.Equal(new NhAssistantSubjectSummaryDto(busyProject.ToString(), 1, 0, true), (await SummarizeAsync(host, ids, Member)).Data!.Items[1]);
        Assert.Equal(new NhAssistantSubjectSummaryDto(busyProject.ToString(), 0, 0, false), (await SummarizeAsync(host, ids, "stranger")).Data!.Items[1]);

        var tooMany = Enumerable.Range(0, 201).Select(index => index.ToString()).ToArray();
        Assert.Equal(NhAssistantErrorCodes.MessageInvalid, Code(await SummarizeAsync(host, tooMany, Approver)));
    }

    private async Task<AssistantTestHost> CreateHostAsync(AssistantTestProvider provider, NhAiScriptedChatClient model)
    {
        return await AssistantTestHost.CreateAsync(
            database,
            provider,
            model,
            configure: services =>
            {
                services.AddSingleton(new TestSubjects());
                services.Replace(ServiceDescriptor.Singleton<INhAiPrincipalFactory, TestPrincipalFactory>());
            },
            assistantBuilder: assistant => assistant
                .AddSubjectType<TestSubjectResolver>()
                .AddApprovalPolicy("project-review", policy => policy
                    .Lifetime(TimeSpan.FromDays(3))
                    .Approvers(NhAiApprovers.SubjectRole(NhAiSubjectRole.Approver))
                    .Mode(NhAiApprovalMode.Deferred)
                    .BindTo(NhAiApprovalBinding.ToolContract))
                .ConfigureAgent(AgentId, agent => agent
                    .Grant("projects.*")
                    .UseApprovalPolicy("project-review")));
    }

    private static async Task StartAndRunAsync(AssistantTestHost host, Guid projectId)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var info = (await services.GetRequiredService<NhAssistantSubjectAccessService>().DescribeAsync(Subject(projectId), CancellationToken.None))!;
        var agent = (await services.GetRequiredService<NhAssistantAgentCatalog>().FindAsync(AgentId, false, CancellationToken.None))!;
        var thread = (await services.GetRequiredService<NhAssistantThreadService>().GetOrCreateAsync(agent, info, CancellationToken.None)).Data!;
        var run = new AssistantRun
        {
            Id = Guid.NewGuid(),
            ConversationId = thread.ConversationId,
            TriggerId = "project-created",
            AccountableOwnerId = thread.AccountableOwnerId,
            Status = NhAssistantRunStatuses.Queued,
            AgentVersion = agent.Definition.Version,
            CreatedAt = DateTimeOffset.UtcNow,
            ConcurrencyStamp = Guid.NewGuid()
        };
        await services.GetRequiredService<NhAssistantRunStore>().AddRunAsync(run, CancellationToken.None);
        var options = services.GetRequiredService<NhAssistantRegistrationState>().AgentRuns[AgentId];
        await services.GetRequiredService<INhAssistantTurnRunner>().RunAgentTurnAsync(
            new NhAssistantAgentTurnRequest(
                new NhAssistantAgentTurnContext(thread, info, options, run.Id, run.TriggerId, null, thread.AccountableOwnerId, IsRequestTurn: false),
                [new NhAssistantAgentTurnInput(Guid.NewGuid(), null, run.TriggerId, "Project PRJ-1 was created.")]),
            CancellationToken.None);
    }

    private static async Task<Common.Models.TaskResult<NhAssistantSubjectWorkDto>> GetAsync(
        AssistantTestHost host,
        Guid projectId,
        string actorId,
        string? agentId = null)
    {
        await using var scope = host.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<INhAssistantSubjectWork>()
            .GetAsync(Subject(projectId), actorId, Principal(actorId), agentId);
    }

    private static async Task<Common.Models.TaskResult<NhAssistantSubjectSummaryListDto>> SummarizeAsync(
        AssistantTestHost host,
        IReadOnlyCollection<string> subjectIds,
        string actorId)
    {
        await using var scope = host.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<INhAssistantSubjectWork>()
            .SummarizeAsync("project", subjectIds, actorId, Principal(actorId));
    }

    private static string? Code(Common.Models.TaskResult result)
    {
        Assert.False(result.Success);
        return result.GetResultItems().Select(item => item.Name).FirstOrDefault(name => !string.IsNullOrWhiteSpace(name));
    }

    private static ClaimsPrincipal Principal(string actorId)
    {
        return new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, actorId)], "test"));
    }

    private static NhAiSubjectRef Subject(Guid projectId)
    {
        return new NhAiSubjectRef("project", projectId.ToString());
    }
}
