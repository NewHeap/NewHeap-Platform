using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.AI.Chat.Runtime;
using NewHeap.Platform.AI.Chat.Tests.Infrastructure;
using NewHeap.Platform.AI.Test;
using Xunit;

namespace NewHeap.Platform.AI.Chat.Tests;

/// <summary>
/// A proposal guard grounds a proposed value in what a tool returned in the same turn, without seeing
/// the result: only successful calls of real tools count, also those before a question the turn waited
/// for, and the guard learns which call holds the value and whether the model passed it in itself.
/// Proved on both relational providers.
/// </summary>
public sealed class AssistantTurnToolResultsTests(AssistantDatabaseFixture database) : IClassFixture<AssistantDatabaseFixture>
{
    private const string AgentId = "project-assistant";
    private const string Returned = "Roadmap project";
    private const string Invented = "Invented Inc";
    private const string SearchFunction = "projects_search_v1";
    private const string ChangeStatusFunction = "projects_change_status_v1";

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_value_a_tool_returned_is_found_with_its_tool_and_a_value_only_the_model_wrote_is_not(AssistantTestProvider provider)
    {
        var model = Script(query: Invented);
        await using var host = await CreateHostAsync(provider, model);
        var conversation = await host.CreateConversationAsync(agentId: AgentId);

        await host.SendAsync(conversation.Id, "Search, then activate the project.");

        var probe = host.Services.GetRequiredService<Probe>();
        var found = probe.Result(Returned);
        var match = Assert.Single(found.Matches);
        Assert.Equal(("projects.search", false), (match.ToolId, match.InArguments));
        Assert.True(found.Complete);
        Assert.Equal(1, found.SearchedCalls);

        // The model passed this value to the search, which did not return it: that is no source.
        Assert.False(probe.Result(Invented).Found);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_guard_can_see_that_the_model_passed_the_value_to_the_call_itself(AssistantTestProvider provider)
    {
        await using var host = await CreateHostAsync(provider, Script(query: Returned));
        var conversation = await host.CreateConversationAsync(agentId: AgentId);

        await host.SendAsync(conversation.Id, "Search, then activate the project.");

        Assert.True(Assert.Single(host.Services.GetRequiredService<Probe>().Result(Returned).Matches).InArguments);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_call_that_failed_is_no_source(AssistantTestProvider provider)
    {
        await using var host = await CreateHostAsync(provider, Script(query: Returned));
        host.Tools.SearchException = new InvalidOperationException(Returned);
        var conversation = await host.CreateConversationAsync(agentId: AgentId);

        await host.SendAsync(conversation.Id, "Search, then activate the project.");

        var search = host.Services.GetRequiredService<Probe>().Result(Returned);
        Assert.False(search.Found);
        Assert.Equal(0, search.SearchedCalls);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task Results_before_a_question_still_count_and_the_answer_is_no_source(AssistantTestProvider provider)
    {
        var model = new NhAiScriptedChatClient()
            .RespondWithFunctionCall(SearchFunction, new { input = new { query = "launch" } }, "call-search")
            .RespondWithFunctionCall("ask_question", new { question = "Which one?", choices = new[] { Invented, "Other" } }, "call-ask")
            .RespondWithFunctionCall(ChangeStatusFunction, new { input = new { projectId = Guid.NewGuid(), status = "Active" } }, "call-change");
        await using var host = await CreateHostAsync(provider, model);
        var conversation = await host.CreateConversationAsync(agentId: AgentId);

        var asking = await host.SendAsync(conversation.Id, "Search, ask, then activate the project.");
        await host.AnswerAsync(conversation.Id, asking.Single<NhAssistantQuestionAskedEvent>().Question.QuestionId, 0);

        var probe = host.Services.GetRequiredService<Probe>();
        Assert.True(probe.Result(Returned).Found);
        // The person chose it, but the model wrote the choice.
        Assert.False(probe.Result(Invented).Found);
    }

    /// <summary>
    /// A tool whose <c>MaxResultBytes</c> is above the storage bound has its result cut when it is stored.
    /// </summary>
    [Fact]
    public void A_result_cut_short_at_the_storage_bound_is_reported_as_incomplete()
    {
        var whole = "[{\"name\":\"" + Returned + new string('x', NhAssistantLimits.MaxStoredJsonCharacters);
        NhAssistantTurnToolResult[] calls =
        [
            new(Guid.NewGuid(), "projects.search", null, whole[..NhAssistantLimits.MaxStoredJsonCharacters])
        ];
        var noScripts = new HashSet<string>();

        var seen = NhAssistantTurnToolResults.Search(calls, Returned, StringComparison.Ordinal, noScripts);
        var notSeen = NhAssistantTurnToolResults.Search(calls, Invented, StringComparison.Ordinal, noScripts);

        Assert.True(seen.Found);
        Assert.False(notSeen.Found);
        Assert.Equal(1, notSeen.CutShortCalls);
        Assert.False(notSeen.Complete);
    }

    [Fact]
    public async Task Outside_a_turn_the_search_is_refused()
    {
        await using var host = await CreateHostAsync(AssistantTestProvider.PostgreSql, new NhAiScriptedChatClient());
        await using var scope = host.Services.CreateAsyncScope();

        var result = await scope.ServiceProvider.GetRequiredService<INhAssistantTurnToolResults>().FindAsync(Returned);

        Assert.False(result.Success);
        Assert.Contains(result.GetResultItems(), item => item.Name == NhAssistantToolResultCodes.NoTurn);
    }

    [Theory]
    [InlineData("{\"a\":\"say \\\"hi\\\" now\"}", "say \"hi\"", false, true)]
    [InlineData("{\"a\":\"caf\\u00e9\"}", "café", false, true)]
    [InlineData("[{\"port\":587}]", "587", false, true)]
    [InlineData("{\"shop.example\":true}", "shop.example", false, true)]
    [InlineData("{\"a\":\"Info@Example.com\"}", "info@example.com", false, false)]
    [InlineData("{\"a\":\"Info@Example.com\"}", "info@example.com", true, true)]
    [InlineData("{\"a\":true}", "true", false, false)]
    [InlineData("{\"a\":\"say \\\"hi\\\" and then it was cu", "say \"hi\"", false, true)]
    [InlineData("plain text result", "text", false, true)]
    public void A_value_is_compared_as_the_tool_returned_it(string json, string value, bool ignoreCase, bool expected)
    {
        var comparison = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        Assert.Equal(expected, NhAssistantTurnToolResults.Holds(json, value, comparison));
    }

    [Theory]
    [InlineData("projects.search", true)]
    [InlineData("mcp.planning.lookup", true)]
    [InlineData("assistant.use-skill", false)]
    [InlineData("assistant.ask-question", false)]
    [InlineData("assistant.memory-update", false)]
    [InlineData("assistant.consult.reviewer.project", false)]
    [InlineData("scripts.run", false)]
    public void What_a_model_wrote_is_no_source(string toolId, bool expected)
    {
        Assert.Equal(expected, NhAssistantTurnToolResults.IsSource(toolId, new HashSet<string> { "scripts.run" }));
    }

    private static NhAiScriptedChatClient Script(string query)
    {
        return new NhAiScriptedChatClient()
            .RespondWithFunctionCall(SearchFunction, new { input = new { query } }, "call-search")
            .RespondWithFunctionCall(ChangeStatusFunction, new { input = new { projectId = Guid.NewGuid(), status = "Active" } }, "call-change");
    }

    private async Task<AssistantTestHost> CreateHostAsync(AssistantTestProvider provider, NhAiScriptedChatClient model)
    {
        return await AssistantTestHost.CreateAsync(
            database,
            provider,
            model,
            configure: services => services.AddSingleton<Probe>(),
            assistantBuilder: assistant => assistant
                .AddProposalGuard<SearchingGuard>([TestProjectToolCatalog.ChangeStatus.Id], NhAiProposalGuardStages.Proposal)
                .ConfigureAgent(AgentId, agent => agent.AllowQuestions()));
    }

    /// <summary>
    /// What the guard found for each value, at the moment the call proposed.
    /// </summary>
    private sealed class Probe
    {
        public Dictionary<string, NhAssistantToolResultSearch> Found { get; } = [];

        public NhAssistantToolResultSearch Result(string value)
        {
            return Found[value];
        }
    }

    private sealed class SearchingGuard(INhAssistantTurnToolResults results, Probe probe) : INhAiProposalGuard
    {
        public ValueTask<IReadOnlyList<NhAiProposalKey>> GetKeysAsync(
            NhAiProposalGuardContext context,
            CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult<IReadOnlyList<NhAiProposalKey>>([]);
        }

        public async ValueTask<NhAiPreconditionResult> CheckAsync(
            NhAiProposalGuardContext context,
            CancellationToken cancellationToken = default)
        {
            foreach (var value in new[] { Returned, Invented })
            {
                var search = await results.FindAsync(value, cancellationToken: cancellationToken);
                Assert.True(search.Success);
                probe.Found[value] = search.Data!;
            }

            return NhAiPreconditionResult.Satisfied;
        }
    }
}
