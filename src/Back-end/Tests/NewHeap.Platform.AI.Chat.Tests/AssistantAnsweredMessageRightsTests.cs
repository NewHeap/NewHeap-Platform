using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NewHeap.Platform.AI.AspNet;
using NewHeap.Platform.AI.Chat.AspNet.AgentRuns;
using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Chat.Runs;
using NewHeap.Platform.AI.Chat.Runtime;
using NewHeap.Platform.AI.Chat.Tests.Infrastructure;
using NewHeap.Platform.AI.Test;
using Xunit;

namespace NewHeap.Platform.AI.Chat.Tests;

/// <summary>
/// A run that answers another person's message, such as one typed while the run worked that the run
/// claimed, borrows neither its initiator's delegated rights nor the gateway-read credential of its
/// initiator or accountable account: a person lends rights only to their own requests. The run's own
/// work keeps them.
/// </summary>
public sealed class AssistantAnsweredMessageRightsTests(AssistantDatabaseFixture database) : IClassFixture<AssistantDatabaseFixture>
{
    private const string AgentId = "project-assistant";
    private const string SearchFunction = "projects_search_v1";
    private const string Starter = "approver-1";
    private const string Member = "member-1";
    private const string Account = "service-1";

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_run_answers_another_persons_message_without_its_initiators_delegated_rights(AssistantTestProvider provider)
    {
        var model = new NhAiScriptedChatClient()
            .RespondWithFunctionCall(SearchFunction, new { input = new { query = "roadmap" } }, "call-own")
            .RespondWithText("I found the roadmap.")
            .RespondWithFunctionCall(SearchFunction, new { input = new { query = "salaries" } }, "call-other")
            .RespondWithText("I searched for it.");
        await using var host = await CreateHostAsync(provider, model, agent => agent.DelegateFromInitiator().Grant("projects.*"));

        // The starter's run answers the starter's own message with the starter's rights.
        await RunTurnAsync(host, initiator: Starter, (Starter, "Find the roadmap."));
        // The same kind of run claimed a member's message typed while it worked: the turn answers the
        // member too, so the starter lends it nothing and only the agent's own grant serves the call.
        await RunTurnAsync(host, initiator: Starter, (Starter, "Find the roadmap."), (Member, "Find the salaries."));

        var calls = host.Audit.Records.Where(record => record.ToolId == "projects.search").Select(record => record.GrantSource).ToArray();
        Assert.Equal([NhAiGrantSource.Delegated, NhAiGrantSource.Agent], calls);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_run_answers_another_persons_message_without_the_gateway_read_credential_of_its_account(AssistantTestProvider provider)
    {
        var model = new NhAiScriptedChatClient()
            .RespondWithFunctionCall(SearchFunction, new { input = new { query = "roadmap" } }, "call-own")
            .RespondWithText("I read the roadmap.")
            .RespondWithFunctionCall(SearchFunction, new { input = new { query = "salaries" } }, "call-other")
            .RespondWithText("I searched for it.");
        await using var host = await CreateHostAsync(
            provider,
            model,
            agent => agent.ServiceAccount(Account).AllowGatewayReads().Grant("projects.*"),
            gatewayReads: true);

        // A run nobody started reads with its account's credential when the account itself asked.
        await RunTurnAsync(host, initiator: null, (Account, "Read the roadmap."));
        // When it answers a member's message, the account's credential reads nothing for the member.
        await RunTurnAsync(host, initiator: null, (Member, "Read the salaries."));

        var calls = host.Audit.Records.Where(record => record.ToolId == "projects.search").Select(record => record.GrantSource).ToArray();
        Assert.Equal([NhAiGrantSource.DelegatedRead, NhAiGrantSource.Agent], calls);
    }

    private async Task<AssistantTestHost> CreateHostAsync(
        AssistantTestProvider provider,
        NhAiScriptedChatClient model,
        Action<NhAssistantAgentRunBuilder> configureAgent,
        bool gatewayReads = false)
    {
        return await AssistantTestHost.CreateAsync(
            database,
            provider,
            model,
            configure: services =>
            {
                services.AddSingleton(new TestSubjects());
                services.Replace(ServiceDescriptor.Singleton<INhAiPrincipalFactory, TestPrincipalFactory>());
                if (gatewayReads)
                {
                    services.AddSingleton<INhAiDelegatedReadSurface, TestReadSurface>();
                    services.AddScoped<INhAiDelegatedReadCredentialIssuer, TestNoCredentialIssuer>();
                }
            },
            assistantBuilder: assistant => assistant
                .AddSubjectType<TestSubjectResolver>()
                .ConfigureAgent(AgentId, configureAgent));
    }

    /// <summary>
    /// One turn of a run of <paramref name="initiator"/>, or of a run nobody started, in a new
    /// project's thread, that answers the messages of <paramref name="messages"/>' authors.
    /// </summary>
    private static async Task RunTurnAsync(AssistantTestHost host, string? initiator, params (string Author, string Text)[] messages)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var info = (await services.GetRequiredService<NhAssistantSubjectAccessService>()
            .DescribeAsync(new NhAiSubjectRef("project", Guid.NewGuid().ToString()), CancellationToken.None))!;
        var agent = (await services.GetRequiredService<NhAssistantAgentCatalog>().FindAsync(AgentId, false, CancellationToken.None))!;
        var thread = (await services.GetRequiredService<NhAssistantThreadService>().GetOrCreateAsync(agent, info, CancellationToken.None)).Data!;
        var triggerId = initiator is null ? "project-created" : NhAssistantTriggerIds.Manual;
        var agentTurn = new NhAssistantAgentTurnContext(
            thread,
            info,
            services.GetRequiredService<NhAssistantRegistrationState>().AgentRuns[AgentId],
            null,
            triggerId,
            initiator,
            thread.AccountableOwnerId,
            IsRequestTurn: false);
        var inputs = messages
            .Select(message => new NhAssistantAgentTurnInput(Guid.NewGuid(), message.Author, NhAssistantTriggerIds.QueuedMessage, message.Text))
            .ToArray();
        var result = await services.GetRequiredService<INhAssistantTurnRunner>().RunAgentTurnAsync(
            new NhAssistantAgentTurnRequest(agentTurn, inputs),
            CancellationToken.None);
        Assert.Equal(NhAssistantTurnStatuses.Completed, result.Outcome);
    }
}
