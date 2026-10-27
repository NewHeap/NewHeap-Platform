using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.AI.AspNet;
using NewHeap.Platform.AI.Chat.AspNet.AgentRuns;
using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Chat.Runs;
using NewHeap.Platform.AI.Chat.Runtime;
using NewHeap.Platform.AI.Chat.Tests.Infrastructure;
using NewHeap.Platform.AI.Test;
using NewHeap.Platform.Common.Models;
using Xunit;

namespace NewHeap.Platform.AI.Chat.Tests;

/// <summary>
/// An approver who decides another person's proposal under the approval policy continues that person's
/// request turn inside the approver's own request. The turn works for its initiator as a headless turn
/// does: a personal MCP server serves it only with the initiator's consented delegated token, never
/// with the approver's ambient token or connection.
/// </summary>
public sealed partial class AssistantMcpCredentialTests
{
    private const string Approver = "approver-1";

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task An_approvers_decision_continues_the_initiators_turn_with_the_initiators_credential_never_the_approvers_token(
        AssistantTestProvider provider)
    {
        var tools = new InMemoryMcpServers();
        await using var remote = await HttpMcpTestServer.StartAsync(() => tools.PlanningTools());
        var projectId = Guid.NewGuid();
        var model = new NhAiScriptedChatClient()
            .RespondWithFunctionCall("projects_change_status_v1", new { input = new { projectId, status = "Active" } }, "call-status")
            .RespondWithFunctionCall(LookupFunction, new { order = "B-200" }, "call-lookup")
            .RespondWithText("The project is active and order B-200 is planned for Monday.");
        var issuer = new RecordingTokenIssuer();
        await using var host = await CreateHostAsync(provider, model: model, issuer: issuer, runs: true);
        await ConnectPersonalServerAsync(host, remote.StreamableUrl);
        Assert.True((await SetConsentAsync(host, Member, allow: true)).Success);
        var thread = await OpenThreadAsync(host, projectId, Member, Approver);

        // The member asks; the change waits for an approver in the member's request turn.
        var paused = await RequestTurnAsync(host, thread, Member, "Activate the project, then look up order B-200.");
        var approval = paused.Single<NhAssistantApprovalRequiredEvent>().Approval;
        var beforeDecision = remote.Requests.Count;
        var issuedBefore = issuer.Requests.Count;

        // The approver approves under the policy; the member's turn continues in the approver's request.
        var resumed = await DecideAsAnotherPersonAsync(host, thread, Approver, approval, initiator: Member);

        Assert.True(resumed.Start.Success);
        Assert.Equal(NhAssistantTurnStatuses.Completed, Assert.IsType<NhAssistantTurnCompletedEvent>(resumed.Events[^1]).Status);
        Assert.Equal(["lookup-order"], tools.Calls);
        var sent = remote.Requests.Skip(beforeDecision).Select(request => request.Authorization).ToArray();
        Assert.NotEmpty(sent);
        Assert.DoesNotContain("Bearer token-of-" + Approver, sent);
        Assert.All(sent, authorization => Assert.Contains(authorization, issuer.Issued.Select(token => "Bearer " + token)));
        var issued = issuer.Requests.Skip(issuedBefore).ToArray();
        Assert.NotEmpty(issued);
        Assert.All(issued, request => Assert.Equal(Member, request.PersonActorId));
        var call = Assert.Single(host.Audit.Records, record => record.ToolId == "mcp.planning.lookup-order");
        Assert.Equal(NhAiGrantSource.Delegated, call.GrantSource);
    }

    /// <summary>
    /// The project's thread with <paramref name="people"/> taking part through their role.
    /// </summary>
    private static async Task<AssistantThread> OpenThreadAsync(AssistantTestHost host, Guid projectId, params string[] people)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var info = (await services.GetRequiredService<NhAssistantSubjectAccessService>()
            .DescribeAsync(new NhAiSubjectRef("project", projectId.ToString()), CancellationToken.None))!;
        var agent = (await services.GetRequiredService<NhAssistantAgentCatalog>().FindAsync(AgentId, false, CancellationToken.None))!;
        var threads = services.GetRequiredService<NhAssistantThreadService>();
        var thread = (await threads.GetOrCreateAsync(agent, info, CancellationToken.None)).Data!;
        foreach (var person in people)
        {
            Assert.NotNull(await threads.ResolveAccessAsync(thread.ConversationId, person, person, CancellationToken.None));
        }

        return thread;
    }

    /// <summary>
    /// A message of <paramref name="actorId"/> in the thread, as <c>POST conversations/{id}/messages</c>
    /// starts it: the person's request turn, in the person's own request.
    /// </summary>
    private static async Task<TurnResult> RequestTurnAsync(AssistantTestHost host, AssistantThread thread, string actorId, string text)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var httpContext = AssistantTestHost.EnterUser(services, actorId);
        var caller = (await services.GetRequiredService<INhAiAuthenticatedInvocationContextResolver>().ResolveAsync(httpContext)).Data;
        var agentTurn = (await services.GetRequiredService<NhAssistantAgentTurnFactory>().CreateAsync(
            thread.ConversationId,
            null,
            NhAssistantConversationKinds.Chat,
            actorId,
            thread.AccountableOwnerId,
            CancellationToken.None)).Data! with { IsRequestTurn = true };
        var started = await services.GetRequiredService<INhAssistantTurnRunner>().StartMessageTurnAsync(
            new NhAssistantMessageTurnRequest(thread.ConversationId, caller, text, Guid.NewGuid().ToString("N"), CancellationToken.None)
            {
                AgentTurn = agentTurn
            },
            CancellationToken.None);
        return await ReadTurnAsync(started);
    }

    /// <summary>
    /// The decision of <paramref name="actorId"/> about <paramref name="initiator"/>'s proposal, as
    /// <c>POST conversations/{id}/approvals/{approvalId}/decide</c> starts it once the approver check
    /// passed: the initiator's request turn, continued in the approver's request.
    /// </summary>
    private static async Task<TurnResult> DecideAsAnotherPersonAsync(
        AssistantTestHost host,
        AssistantThread thread,
        string actorId,
        NhAssistantApprovalView approval,
        string initiator)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var httpContext = AssistantTestHost.EnterUser(services, actorId);
        var caller = (await services.GetRequiredService<INhAiAuthenticatedInvocationContextResolver>().ResolveAsync(httpContext)).Data;
        var agentTurn = (await services.GetRequiredService<NhAssistantAgentTurnFactory>().CreateAsync(
            thread.ConversationId,
            null,
            NhAssistantConversationKinds.Chat,
            initiator,
            thread.AccountableOwnerId,
            CancellationToken.None)).Data! with { IsRequestTurn = true };
        var started = await services.GetRequiredService<INhAssistantTurnRunner>().StartDecisionTurnAsync(
            new NhAssistantDecisionTurnRequest(
                thread.ConversationId,
                caller,
                approval.ApprovalId,
                true,
                approval.ProposalHash,
                null,
                CancellationToken.None)
            {
                AgentTurn = agentTurn,
                PolicyAuthorized = true
            },
            CancellationToken.None);
        return await ReadTurnAsync(started);
    }

    private static async Task<TurnResult> ReadTurnAsync(TaskResult<NhAssistantTurnHandle> started)
    {
        if (!started.Success)
        {
            return new TurnResult(started, []);
        }

        var events = new List<NhAssistantTurnEvent>();
        await foreach (var evt in started.Data.Events.ReadAllAsync())
        {
            events.Add(evt);
        }
        await started.Data.Completion;
        return new TurnResult(started, events);
    }
}
