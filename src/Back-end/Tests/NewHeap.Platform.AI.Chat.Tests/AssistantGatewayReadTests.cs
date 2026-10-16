using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using NewHeap.Platform.AI.AspNet;
using NewHeap.Platform.AI.Chat.AspNet;
using NewHeap.Platform.AI.Chat.AspNet.AgentRuns;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.AI.Chat.Runs;
using NewHeap.Platform.AI.Chat.Runtime;
using NewHeap.Platform.AI.Chat.Tests.Infrastructure;
using NewHeap.Platform.AI.Test;
using NewHeap.Platform.Common.Models;
using Xunit;

namespace NewHeap.Platform.AI.Chat.Tests;

/// <summary>
/// Gateway reads in headless runs, as the assistant sees them: only agents that allow them and
/// answer to an account read, with the rights of the person who started the run or else of that
/// account, and only that person sees what the read returned. The bridge itself is replaced by a
/// read surface over the test catalog's search tool.
/// </summary>
[Collection(AssistantDatabaseCollection.Name)]
public sealed class AssistantGatewayReadTests(AssistantDatabaseFixture database)
{
    private const string AgentId = "project-assistant";

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer, NhAiElevatedReads.Restricted)]
    [InlineData(AssistantTestProvider.PostgreSql, NhAiElevatedReads.Deny)]
    public async Task A_trigger_run_reads_for_its_accountable_account_and_only_that_account_sees_the_result(
        AssistantTestProvider provider,
        NhAiElevatedReads elevatedReads)
    {
        var model = new NhAiScriptedChatClient()
            .RespondWithFunctionCall("projects_search_v1", new { input = new { query = "roadmap" } }, "call-1")
            .RespondWithText("Read the roadmap.");
        await using var host = await CreateHostAsync(provider, model, agent => agent.AllowGatewayReads().ElevatedReads(elevatedReads));
        var started = await StartAsync(host);

        var result = await RunTurnAsync(host, started, initiator: null);

        // Whatever the agent's elevated reads allow, the read counts as restricted and only the
        // account it read for sees the result; a member with the resource's permission does not.
        Assert.True(result.UsedRestrictedResults);
        var forAccount = await ToolsAsync(host, started, Person("service-1"));
        var forMember = await ToolsAsync(host, started, Person("member-1", "project.read"));
        var read = Assert.Single(forAccount);
        Assert.Equal(NhAiGrantSource.DelegatedRead, read.GrantSource);
        Assert.False(read.Restricted);
        Assert.NotNull(read.ResultPreview);
        Assert.True(Assert.Single(forMember).Restricted);
        Assert.Null(forMember[0].ResultPreview);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_run_that_a_person_started_reads_with_their_rights_and_only_they_see_the_result(AssistantTestProvider provider)
    {
        var model = new NhAiScriptedChatClient()
            .RespondWithFunctionCall("projects_search_v1", new { input = new { query = "roadmap" } }, "call-1")
            .RespondWithText("Read the roadmap.");
        await using var host = await CreateHostAsync(provider, model, agent => agent.AllowGatewayReads());
        var started = await StartAsync(host);

        await RunTurnAsync(host, started, initiator: "member-1");

        Assert.False(Assert.Single(await ToolsAsync(host, started, Person("member-1"))).Restricted);
        Assert.True(Assert.Single(await ToolsAsync(host, started, Person("service-1"))).Restricted);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task Without_the_opt_in_the_run_reads_with_its_grant_as_before(AssistantTestProvider provider)
    {
        var model = new NhAiScriptedChatClient()
            .RespondWithFunctionCall("projects_search_v1", new { input = new { query = "roadmap" } }, "call-1")
            .RespondWithText("Read the roadmap.");
        await using var host = await CreateHostAsync(provider, model, agent => agent.Grant("projects.*"));
        var started = await StartAsync(host);

        await RunTurnAsync(host, started);

        await using var scope = host.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var conversation = (await services.GetRequiredService<INhAssistantStore>()
            .FindShareableConversationAsync(started.ConversationId!.Value, CancellationToken.None))!;
        var view = await services.GetRequiredService<NhAssistantConversationReader>().CreateViewAsync(conversation, CancellationToken.None);
        Assert.Equal(NhAiGrantSource.Agent, Assert.Single(Tools(view)).GrantSource);
    }

    [Fact]
    public async Task Startup_fails_for_an_agent_that_answers_to_itself_or_without_an_issuer()
    {
        await using (var selfAccountable = await CreateHostAsync(
            AssistantTestProvider.PostgreSql,
            new NhAiScriptedChatClient(),
            agent => agent.Accountability(NhAiAgentAccountability.Agent).AllowGatewayReads()))
        {
            var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => StartValidatorAsync(selfAccountable));
            Assert.Contains("answers to itself", failure.Message);
        }

        await using (var withoutIssuer = await CreateHostAsync(
            AssistantTestProvider.PostgreSql,
            new NhAiScriptedChatClient(),
            agent => agent.AllowGatewayReads(),
            issuer: false))
        {
            var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => StartValidatorAsync(withoutIssuer));
            Assert.Contains("AddNewHeapPlatformAIDelegatedReads", failure.Message);
        }
    }

    /// <summary>The thread's tool calls as <paramref name="viewer"/> sees them.</summary>
    private static async Task<NhAssistantToolCallPartView[]> ToolsAsync(AssistantTestHost host, NhAssistantTriggerReceipt started, ClaimsPrincipal viewer)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var conversationId = started.ConversationId!.Value;
        var threadAccess = (await services.GetRequiredService<NhAssistantThreadService>()
            .ResolveAccessAsync(conversationId, "member-1", null, CancellationToken.None))!;
        var conversation = (await services.GetRequiredService<INhAssistantStore>()
            .FindShareableConversationAsync(conversationId, CancellationToken.None))!;
        var view = await services.GetRequiredService<NhAssistantConversationReader>().CreateViewAsync(conversation, CancellationToken.None);
        return Tools(await NhAssistantRestrictedResults.ApplyAsync(view, new DefaultHttpContext { RequestServices = services, User = viewer }, threadAccess));
    }

    private static Task StartValidatorAsync(AssistantTestHost host)
    {
        return host.Services.GetServices<IHostedService>().OfType<NhAssistantStartupValidator>().Single().StartAsync(CancellationToken.None);
    }

    private static NhAssistantToolCallPartView[] Tools(NhAssistantConversationView view)
    {
        return view.Messages.SelectMany(message => message.Parts).OfType<NhAssistantToolCallPartView>().ToArray();
    }

    private static ClaimsPrincipal Person(string actorId, params string[] permissions)
    {
        return new ClaimsPrincipal(new ClaimsIdentity(
            permissions.Select(permission => new Claim("permission", permission)).Prepend(new Claim(ClaimTypes.NameIdentifier, actorId)),
            "test"));
    }

    private async Task<AssistantTestHost> CreateHostAsync(
        AssistantTestProvider provider,
        NhAiScriptedChatClient model,
        Action<NhAssistantAgentRunBuilder> configureAgent,
        bool issuer = true)
    {
        return await AssistantTestHost.CreateAsync(
            database,
            provider,
            model,
            configure: services =>
            {
                services.AddSingleton(new TestSubjects());
                services.AddSingleton<RecordingOperations>();
                services.AddSingleton(provider => provider.GetRequiredService<RecordingOperations>().Service);
                services.Replace(ServiceDescriptor.Singleton<INhAiPrincipalFactory, TestPrincipalFactory>());
                services.AddSingleton<INhAiDelegatedReadSurface, TestReadSurface>();
                if (issuer)
                {
                    services.AddScoped<INhAiDelegatedReadCredentialIssuer, TestNoCredentialIssuer>();
                }
            },
            assistantBuilder: assistant => assistant
                .AddSubjectType<TestSubjectResolver>()
                .ConfigureAgent(AgentId, agent =>
                {
                    agent.ServiceAccount("service-1");
                    configureAgent(agent);
                })
                .AddTrigger<ProjectChanged>("project-changed", trigger => trigger
                    .StartAgent(AgentId)
                    .Subject(evt => new NhAiSubjectRef("project", evt.ProjectId.ToString("N")))
                    .Message(evt => "The project changed: " + evt.Change)
                    .Dedupe(evt => evt.EventId.ToString("N"))
                    .OnBusy(NhAssistantTriggerConflict.Queue)));
    }

    private static async Task<NhAssistantTriggerReceipt> StartAsync(AssistantTestHost host)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var receipt = Assert.Single(await scope.ServiceProvider.GetRequiredService<INhAssistantAgentTriggers>()
            .PublishAsync(new ProjectChanged(Guid.NewGuid(), Guid.NewGuid(), "created")));
        Assert.NotNull(receipt.RunId);
        return receipt;
    }

    private static async Task<NhAssistantAgentTurnResult> RunTurnAsync(AssistantTestHost host, NhAssistantTriggerReceipt receipt, string? initiator = null)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var runs = services.GetRequiredService<NhAssistantRunStore>();
        var thread = (await runs.FindThreadByConversationAsync(receipt.ConversationId!.Value, CancellationToken.None))!;
        var run = (await runs.FindRunAsync(receipt.RunId!.Value, CancellationToken.None))!;
        var subject = (await services.GetRequiredService<NhAssistantSubjectAccessService>()
            .DescribeAsync(new NhAiSubjectRef(thread.SubjectType, thread.SubjectId), CancellationToken.None))!;
        var options = services.GetRequiredService<NhAssistantRegistrationState>().AgentRuns[AgentId];
        var result = await services.GetRequiredService<INhAssistantTurnRunner>().RunAgentTurnAsync(
            new NhAssistantAgentTurnRequest(
                new NhAssistantAgentTurnContext(thread, subject, options, run.Id, run.TriggerId, initiator, thread.AccountableOwnerId, IsRequestTurn: false),
                [new NhAssistantAgentTurnInput(Guid.NewGuid(), null, run.TriggerId, "Read the roadmap.")]),
            CancellationToken.None);
        Assert.Equal(NhAssistantTurnStatuses.Completed, result.Outcome);
        return result;
    }
}

/// <summary>
/// Treats the test catalog's search as a gateway read; it makes no HTTP call.
/// </summary>
public sealed class TestReadSurface : INhAiDelegatedReadSurface
{
    public bool IsReadTool(NhAiToolDescriptor descriptor)
    {
        return descriptor.Id == "projects.search";
    }

    public bool IsReadEndpoint(Endpoint endpoint, string actionToolId)
    {
        return false;
    }
}

/// <summary>An issuer that is never asked: the test search makes no HTTP call.</summary>
public sealed class TestNoCredentialIssuer : INhAiDelegatedReadCredentialIssuer
{
    public ValueTask<TaskResult<NhAiDelegatedReadCredential>> IssueAsync(
        NhAiDelegatedReadRequest request,
        CancellationToken cancellationToken)
    {
        return ValueTask.FromResult(TaskResult<NhAiDelegatedReadCredential>.Failed("unused", "Not used."));
    }
}
