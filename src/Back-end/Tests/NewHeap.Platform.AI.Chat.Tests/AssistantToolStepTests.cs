using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.AI.Chat.Runtime;
using NewHeap.Platform.AI.Chat.Tests.Infrastructure;
using NewHeap.Platform.AI.Test;
using Xunit;

namespace NewHeap.Platform.AI.Chat.Tests;

/// <summary>
/// The step runtime of compositions and scripts (<see cref="INhAssistantToolSteps"/>): every step runs
/// through the shared invoker with the turn's context and gets the decision the same call gets on its
/// own, is marked as a step of the call that ran it on its context and audit record, and is a call of
/// the turn with a row of its own; an inline approval stops the chain and the turn waits; outside a turn
/// a step runs through its governed function. Both relational providers where a turn persists.
/// </summary>
public sealed class AssistantToolStepTests(AssistantDatabaseFixture database) : IClassFixture<AssistantDatabaseFixture>
{
    private const string ChainFunction = "projects_chain_v1";
    private const string SearchFunction = "projects_search_v1";
    private const string ChangeStatusFunction = "projects_change_status_v1";

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_chain_runs_each_step_through_the_invoker_with_the_turns_context_and_marks_it_as_a_step(AssistantTestProvider provider)
    {
        var model = new NhAiScriptedChatClient()
            .RespondWithFunctionCall(ChainFunction, Steps(
                (SearchFunction, new { query = "roadmap" }),
                (SearchFunction, new { input = new { query = "road" } }),
                ("unknown_tool", new { })), "call-chain")
            .RespondWithText("Two projects.");
        await using var host = await CreateHostAsync(provider, model);
        var conversation = await host.CreateConversationAsync();

        await host.SendAsync(conversation.Id, "Find the roadmap projects.");

        var rows = await RowsAsync(host, conversation.Id);
        var parent = Assert.Single(rows, row => row.FunctionName == ChainFunction);
        Assert.Equal(NhAssistantToolCallStatuses.Succeeded, parent.Status);
        var steps = rows
            .Where(row => row.CallId.StartsWith(NhAssistantToolStepCallIds.PrefixOf(NhAiToolCallOriginKind.Composition, parent.Id), StringComparison.Ordinal))
            .OrderBy(row => row.CallId, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal([TestProjectToolCatalog.Search.Id, TestProjectToolCatalog.Search.Id], steps.Select(row => row.ToolId).ToArray());
        Assert.All(steps, row => Assert.Equal(NhAssistantToolCallStatuses.Succeeded, row.Status));
        Assert.Equal(rows.Count, steps.Length + 1);

        // Each step ran with the turn's context and says whose step it is.
        var contexts = host.Tools.Contexts.ToArray();
        Assert.Equal(2, contexts.Length);
        for (var index = 0; index < contexts.Length; index++)
        {
            var origin = Assert.IsType<NhAiToolCallOrigin>(contexts[index].Origin);
            Assert.Equal(NhAiToolCallOriginKind.Composition, origin.Kind);
            Assert.Equal(TestChainToolCatalog.Chain.Id, origin.ToolId);
            Assert.Equal(TestChainToolCatalog.Chain.ContractHash, origin.Hash);
            Assert.Equal(parent.Id, origin.ParentInvocationId);
            Assert.Equal(index, origin.StepIndex);
            Assert.Equal(steps[index].Id, contexts[index].InvocationId);
            Assert.Equal(conversation.Id, contexts[index].ConversationId);
            Assert.Equal(NhAiActorKind.Agent, contexts[index].ActorKind);
        }

        // The steps are audited under the call that ran them; the call has a record of its own.
        var stepAudits = host.Audit.Records.Where(record => record.ToolId == TestProjectToolCatalog.Search.Id).ToArray();
        Assert.Equal(2, stepAudits.Length);
        Assert.All(stepAudits, record => Assert.Equal(parent.Id, record.Origin?.ParentInvocationId));
        var chainAudit = Assert.Single(host.Audit.Records, record => record.ToolId == TestChainToolCatalog.Chain.Id);
        Assert.Equal(NhAiOutcomeKind.Succeeded, chainAudit.Outcome);
        Assert.Equal(parent.Id, chainAudit.InvocationId);
        Assert.Null(chainAudit.Origin);

        // The refused step reached no tool, and the turn counts the two that ran.
        var session = Assert.Single(host.Services.GetRequiredService<TestChainRecorder>().Sessions);
        Assert.Equal(
            [NhAssistantToolStepStatus.Succeeded, NhAssistantToolStepStatus.Succeeded, NhAssistantToolStepStatus.Refused],
            session.Steps.Select(step => step.Status).ToArray());
        Assert.Equal(NhAssistantToolStepCodes.ToolUnavailable, session.Steps[2].Code);
        Assert.DoesNotContain(session.Tools, tool => tool.Id == TestChainToolCatalog.Chain.Id);
        var activity = Assert.Single(host.Services.GetRequiredService<TestChainRecorder>().Activities);
        Assert.Equal(2, activity.ToolCalls.Count);
        Assert.Equal(1, activity.RoundsSaved);
        Assert.Equal(2, await ToolCallsOfAssistantMessageAsync(host, conversation.Id));
        Assert.Single(model.FunctionResults);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task An_inline_step_stops_the_chain_and_the_turn_continues_after_the_decision_with_the_chain_and_the_step(AssistantTestProvider provider)
    {
        var projectId = Guid.NewGuid();
        var model = new NhAiScriptedChatClient()
            .RespondWithFunctionCall(ChainFunction, Steps(
                (SearchFunction, new { query = "roadmap" }),
                (ChangeStatusFunction, new { projectId, status = "Closed" }),
                (SearchFunction, new { query = "after" })), "call-chain")
            .RespondWithText("The project is closed.");
        await using var host = await CreateHostAsync(provider, model);
        var conversation = await host.CreateConversationAsync();

        var waiting = await host.SendAsync(conversation.Id, "Close the roadmap project.");

        // A person's chat decides inline: the chain stops at the change, nothing after it runs.
        var approval = waiting.Single<NhAssistantApprovalRequiredEvent>().Approval;
        Assert.Empty(host.Tools.StatusChanges);
        var session = Assert.Single(host.Services.GetRequiredService<TestChainRecorder>().Sessions);
        Assert.True(session.Stopped);
        Assert.Equal(1, session.AwaitingApproval?.Index);
        Assert.Equal(approval.ProposalId, session.AwaitingApproval?.ProposalId);
        Assert.Equal(
            [NhAssistantToolStepStatus.Succeeded, NhAssistantToolStepStatus.AwaitingApproval, NhAssistantToolStepStatus.Refused],
            session.Steps.Select(step => step.Status).ToArray());
        Assert.Equal(NhAssistantToolStepCodes.ChainStopped, session.Steps[2].Code);
        Assert.Equal(NhAssistantConversationStatuses.WaitingForApproval, (await host.ReloadAsync(conversation.Id)).Status);

        var rows = await RowsAsync(host, conversation.Id);
        var parent = Assert.Single(rows, row => row.FunctionName == ChainFunction);
        // The chain did what it could; the decision belongs to its step.
        Assert.Equal(NhAssistantToolCallStatuses.Succeeded, parent.Status);
        var stored = await host.Services.GetRequiredService<INhAssistantStore>().FindApprovalAsync(approval.ApprovalId, CancellationToken.None);
        var waitingStep = Assert.Single(rows, row => row.Id == stored!.ToolInvocationId);
        Assert.Equal(NhAssistantToolStepCallIds.Of(NhAiToolCallOriginKind.Composition, parent.Id, 1), waitingStep.CallId);
        Assert.Equal(NhAssistantToolCallStatuses.AwaitingApproval, waitingStep.Status);

        var decided = await host.DecideAsync(conversation.Id, approval, approve: true);

        Assert.Null(decided.StartCode);
        Assert.Equal(new ProjectStatusInput(projectId, "Closed"), Assert.Single(host.Tools.StatusChanges));
        // The model continues with its own call and where the chain stopped, then the decided step.
        var calls = model.Requests[^1].Messages
            .SelectMany(message => message.Contents.OfType<FunctionCallContent>())
            .Select(call => call.CallId)
            .ToArray();
        Assert.Equal(["call-chain", waitingStep.CallId], calls);
        Assert.Equal(NhAssistantConversationStatuses.Idle, (await host.ReloadAsync(conversation.Id)).Status);
    }

    [Fact]
    public async Task Outside_a_turn_a_step_runs_through_its_governed_function_and_one_that_needs_approval_fails()
    {
        await using var host = await CreateHostAsync(AssistantTestProvider.PostgreSql, new NhAiScriptedChatClient());
        await using var scope = host.Services.CreateAsyncScope();
        AssistantTestHost.EnterUser(scope.ServiceProvider, AssistantTestHost.UserId);
        var chain = scope.ServiceProvider.GetServices<INhAiToolCatalog>().OfType<TestChainToolCatalog>().Single()
            .CreateFunctions(scope.ServiceProvider)
            .Single();

        var result = await chain.InvokeAsync(
            Arguments(Steps((SearchFunction, new { query = "roadmap" }), (ChangeStatusFunction, new { projectId = Guid.NewGuid(), status = "Closed" }))),
            CancellationToken.None);

        // Outside a turn there is no proposal: the change fails as the same call would.
        var session = Assert.Single(host.Services.GetRequiredService<TestChainRecorder>().Sessions);
        Assert.Equal([NhAssistantToolStepStatus.Succeeded, NhAssistantToolStepStatus.Failed], session.Steps.Select(step => step.Status).ToArray());
        Assert.Equal(NhAiToolFailureCodes.ApprovalRequired, session.Steps[1].Code);
        Assert.Empty(host.Tools.StatusChanges);
        var context = Assert.Single(host.Tools.Contexts);
        Assert.Equal(NhAiToolCallOriginKind.Composition, context.Origin?.Kind);
        Assert.Equal(0, context.Origin?.StepIndex);
        var chainAudit = Assert.Single(host.Audit.Records, record => record.ToolId == TestChainToolCatalog.Chain.Id);
        Assert.Equal(chainAudit.InvocationId, context.Origin?.ParentInvocationId);
        Assert.All(
            host.Audit.Records.Where(record => record.ToolId == TestProjectToolCatalog.ChangeStatus.Id),
            record => Assert.Equal(chainAudit.InvocationId, record.Origin?.ParentInvocationId));
        Assert.Contains("\"success\":true", JsonSerializer.Serialize(result));

        // A read-only session refuses every changing step before it reaches its tool.
        await chain.InvokeAsync(
            Arguments(Steps((ChangeStatusFunction, new { projectId = Guid.NewGuid(), status = "Closed" })), maxMutationSteps: 0),
            CancellationToken.None);
        var readOnly = host.Services.GetRequiredService<TestChainRecorder>().Sessions.Last();
        var refused = Assert.Single(readOnly.Steps);
        Assert.Equal(NhAssistantToolStepStatus.Refused, refused.Status);
        Assert.Equal(NhAssistantToolStepCodes.MutationLimitReached, refused.Code);
        Assert.Single(host.Audit.Records, record => record.ToolId == TestProjectToolCatalog.ChangeStatus.Id);
    }

    internal static object Steps(params (string Tool, object Arguments)[] steps)
    {
        return new
        {
            input = new
            {
                steps = steps.Select(step => new { tool = step.Tool, arguments = step.Arguments }).ToArray()
            }
        };
    }

    private static AIFunctionArguments Arguments(object steps, int? maxMutationSteps = null)
    {
        var input = JsonSerializer.SerializeToElement(steps).GetProperty("input");
        if (maxMutationSteps is { } limit)
        {
            var node = System.Text.Json.Nodes.JsonNode.Parse(input.GetRawText())!.AsObject();
            node["maxMutationSteps"] = limit;
            input = JsonSerializer.SerializeToElement(node);
        }

        return new AIFunctionArguments(new Dictionary<string, object?> { ["input"] = input });
    }

    internal static async Task<List<AssistantToolInvocation>> RowsAsync(AssistantTestHost host, Guid conversationId)
    {
        await using var context = host.Services.GetRequiredService<NhAssistantDbContextFactory>().CreateDbContext();
        return await context.ToolInvocations
            .Where(invocation => invocation.ConversationId == conversationId)
            .ToListAsync();
    }

    private static async Task<int> ToolCallsOfAssistantMessageAsync(AssistantTestHost host, Guid conversationId)
    {
        await using var context = host.Services.GetRequiredService<NhAssistantDbContextFactory>().CreateDbContext();
        return await context.Messages
            .Where(message => message.ConversationId == conversationId && message.Role == NhAssistantMessageRoles.Assistant)
            .Select(message => message.ToolCalls)
            .SingleAsync();
    }

    private Task<AssistantTestHost> CreateHostAsync(AssistantTestProvider provider, NhAiScriptedChatClient model)
    {
        return AssistantTestHost.CreateAsync(
            database,
            provider,
            model,
            configure: services =>
            {
                services.AddSingleton<TestChainRecorder>();
                services.AddNewHeapPlatformAI(ai => ai.AddGeneratedToolCatalog<TestChainToolCatalog>());
                services.AddSingleton<IConfiguration>(new ConfigurationBuilder()
                    .AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["NewHeap:AI:Assistant:Enabled"] = "true",
                        ["NewHeap:AI:Assistant:Compositions"] = "true"
                    })
                    .Build());
            },
            assistantBuilder: assistant => assistant
                .ConfigureAgentContext(AssistantTestData.Agent().Id, context => context.AllowCompositions()));
    }
}
