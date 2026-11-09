using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.AI.Chat.Runtime;
using NewHeap.Platform.AI.Chat.Tests.Infrastructure;
using NewHeap.Platform.AI.Test;
using Xunit;

namespace NewHeap.Platform.AI.Chat.Tests;

/// <summary>
/// A chat that starts by choosing a skill (<c>SkillsInChat(routesFirst: true)</c>): the first model
/// round offers <c>use_skill</c> and <c>ask_question</c> and none of the agent's own or MCP tools, the
/// agent's tools are reachable only inside a skill, the first choice is no switch, and a turn that
/// waited continues where it was. Proved on both relational providers.
/// </summary>
public sealed class AssistantChatRoutesFirstTests(AssistantDatabaseFixture database) : IClassFixture<AssistantDatabaseFixture>
{
    private const string AgentId = "project-assistant";
    private const string Search = "search";
    private const string Change = "change";
    private const string Browse = "browse";
    private const string SearchFunction = "projects_search_v1";
    private const string ChangeStatusFunction = "projects_change_status_v1";

    private static readonly string[] OnlyRoutes = ["ask_question", "use_skill"];

    private static readonly string[] Choices = ["Yes", "No"];

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task The_first_round_offers_only_the_choice_of_a_skill_and_the_skill_brings_its_tools(AssistantTestProvider provider)
    {
        var model = new NhAiScriptedChatClient()
            .RespondWithFunctionCall("use_skill", new { skill = Search, reason = "The person asks for a project." }, "call-route")
            .RespondWithText("I found the launch project.");
        await using var host = await CreateHostAsync(provider, model);
        var conversation = await host.CreateConversationAsync(agentId: AgentId);

        var turn = await host.SendAsync(conversation.Id, "Find the launch project.");

        Assert.Equal(NhAssistantTurnStatuses.Completed, turn.Single<NhAssistantTurnCompletedEvent>().Status);
        Assert.Equal(OnlyRoutes, ToolNames(model.Requests[0]));
        Assert.Contains(
            "No other tool is available",
            model.Requests[0].Options!.Tools!.Single(tool => tool.Name == "use_skill").Description);
        Assert.Contains(SearchFunction, ToolNames(model.Requests[1]));
        Assert.DoesNotContain(ChangeStatusFunction, ToolNames(model.Requests[1]));
        Assert.Equal(Search, (await host.ReadViewAsync(conversation.Id)).Messages.Last().SkillId);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_turn_that_needs_no_tool_answers_without_loading_a_skill_or_the_mcp_tools(AssistantTestProvider provider)
    {
        var model = new NhAiScriptedChatClient().RespondWithText("Hello.");
        var mcp = new CountingToolSource();
        await using var host = await CreateHostAsync(
            provider,
            model,
            configure: services => services.Replace(ServiceDescriptor.Scoped<INhAssistantMcpToolSource>(_ => mcp)));
        await AssignMcpServerAsync(host);
        var conversation = await host.CreateConversationAsync(agentId: AgentId);

        var turn = await host.SendAsync(conversation.Id, "Hello.");

        Assert.Equal(NhAssistantTurnStatuses.Completed, turn.Single<NhAssistantTurnCompletedEvent>().Status);
        Assert.Equal(OnlyRoutes, ToolNames(Assert.Single(model.Requests)));
        Assert.Equal(0, mcp.Calls);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task The_first_choice_is_no_switch_so_two_switches_remain_after_it(AssistantTestProvider provider)
    {
        var model = new NhAiScriptedChatClient()
            .RespondWithFunctionCall("use_skill", new { skill = Search, reason = "First guess." }, "call-1")
            .RespondWithFunctionCall("use_skill", new { skill = Change, reason = "It was not a search." }, "call-2")
            .RespondWithFunctionCall("use_skill", new { skill = Browse, reason = "It was a question about projects." }, "call-3")
            .RespondWithText("Done.");
        await using var host = await CreateHostAsync(provider, model);
        var conversation = await host.CreateConversationAsync(agentId: AgentId);

        await host.SendAsync(conversation.Id, "Help me with the launch project.");

        Assert.Equal(4, model.Requests.Count);
        Assert.Contains("use_skill", ToolNames(model.Requests[2]));
        Assert.DoesNotContain("use_skill", ToolNames(model.Requests[3]));
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_question_asked_before_the_choice_leaves_the_turn_choosing(AssistantTestProvider provider)
    {
        var model = new NhAiScriptedChatClient()
            .RespondWithFunctionCall("ask_question", new { question = "Which project?", choices = Choices }, "call-ask")
            .RespondWithText("Thanks.");
        await using var host = await CreateHostAsync(provider, model);
        var conversation = await host.CreateConversationAsync(agentId: AgentId);

        var asking = await host.SendAsync(conversation.Id, "Help me.");
        await host.AnswerAsync(conversation.Id, asking.Single<NhAssistantQuestionAskedEvent>().Question.QuestionId, 0);

        Assert.Equal(OnlyRoutes, ToolNames(model.Requests[1]));
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_question_does_not_give_back_the_switches_of_the_turn(AssistantTestProvider provider)
    {
        var model = new NhAiScriptedChatClient()
            .RespondWithFunctionCall("use_skill", new { skill = Search, reason = "First guess." }, "call-1")
            .RespondWithFunctionCall("use_skill", new { skill = Change, reason = "It was not a search." }, "call-2")
            .RespondWithFunctionCall("ask_question", new { question = "Go on?", choices = Choices }, "call-ask")
            .RespondWithFunctionCall("use_skill", new { skill = Browse, reason = "It was a question about projects." }, "call-3")
            .RespondWithText("Done.");
        await using var host = await CreateHostAsync(provider, model);
        var conversation = await host.CreateConversationAsync(agentId: AgentId);

        var asking = await host.SendAsync(conversation.Id, "Help me with the launch project.");
        await host.AnswerAsync(conversation.Id, asking.Single<NhAssistantQuestionAskedEvent>().Question.QuestionId, 0);

        // The answer continues in the skill with one switch left; after it, none.
        Assert.Contains(ChangeStatusFunction, ToolNames(model.Requests[3]));
        Assert.Contains("use_skill", ToolNames(model.Requests[3]));
        Assert.DoesNotContain("use_skill", ToolNames(model.Requests[4]));
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_decision_continues_in_the_skill_and_does_not_offer_the_agents_own_tools(AssistantTestProvider provider)
    {
        var model = new NhAiScriptedChatClient()
            .RespondWithFunctionCall("use_skill", new { skill = Change, reason = "The person wants a change." }, "call-route")
            .RespondWithFunctionCall(ChangeStatusFunction, new { input = new { projectId = Guid.NewGuid(), status = "Active" } }, "call-change")
            .RespondWithText("The project is active.");
        await using var host = await CreateHostAsync(provider, model);
        var conversation = await host.CreateConversationAsync(agentId: AgentId);

        var paused = await host.SendAsync(conversation.Id, "Activate the project.");
        var approval = paused.Single<NhAssistantApprovalRequiredEvent>().Approval;
        var resumed = await host.DecideAsync(conversation.Id, approval, approve: true);

        Assert.Equal(NhAssistantTurnStatuses.Completed, resumed.Single<NhAssistantTurnCompletedEvent>().Status);
        var afterDecision = model.Requests[2];
        Assert.Contains(ChangeStatusFunction, ToolNames(afterDecision));
        Assert.DoesNotContain(SearchFunction, ToolNames(afterDecision));
        Assert.Equal(Change, (await host.ReadViewAsync(conversation.Id)).Messages.Last().SkillId);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task Without_a_live_skill_the_turn_has_no_tools_and_says_so_in_the_log(AssistantTestProvider provider)
    {
        var model = new NhAiScriptedChatClient().RespondWithText("I cannot do that now.");
        await using var host = await AssistantTestHost.CreateAsync(
            database,
            provider,
            model,
            assistantBuilder: assistant => assistant.ConfigureAgent(AgentId, agent => agent.SkillsInChat(routesFirst: true)));
        var conversation = await host.CreateConversationAsync(agentId: AgentId);

        await host.SendAsync(conversation.Id, "Find the launch project.");

        Assert.Empty(ToolNames(Assert.Single(model.Requests)));
        Assert.Single(host.Logs.Messages, message => message.Contains("none is active and live", StringComparison.Ordinal));
    }

    [Fact]
    public void Routes_first_is_part_of_offering_the_skills()
    {
        var state = new NhAssistantRegistrationState();
        state.AddAgent(AssistantTestData.Agent());
        var options = state.GetOrAddAgentRun(AgentId);
        var run = new NhAssistantAgentRunBuilder(options);

        run.SkillsInChat(routesFirst: true);
        Assert.True(options.ChatRoutesFirst);

        run.SkillsInChat(enabled: false, routesFirst: true);
        Assert.False(options.ChatRoutesFirst);

        run.SkillsInChat();
        Assert.False(options.ChatRoutesFirst);
    }

    private async Task<AssistantTestHost> CreateHostAsync(
        AssistantTestProvider provider,
        NhAiScriptedChatClient model,
        Action<IServiceCollection>? configure = null)
    {
        return await AssistantTestHost.CreateAsync(
            database,
            provider,
            model,
            configure: configure,
            assistantBuilder: assistant => assistant.ConfigureAgent(AgentId, agent => agent
                .Skill(Search, skill => skill.Description("Searches projects.").Tools("projects.search"))
                .Skill(Change, skill => skill.Description("Changes the status of projects.").Tools("projects.change-status"))
                .Skill(Browse, skill => skill.Description("Answers questions about projects.").Tools("projects.search"))
                .SkillsInChat(routesFirst: true)
                .AllowQuestions()));
    }

    private static async Task AssignMcpServerAsync(AssistantTestHost host)
    {
        var now = DateTimeOffset.UtcNow;
        await using (var scope = host.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<NhAssistantAgentCatalog>()
                .FindAsync(AgentId, includeDisabled: true, CancellationToken.None);
        }

        await using var context = host.Services.GetRequiredService<NhAssistantDbContextFactory>().CreateDbContext();
        context.McpServers.Add(new AssistantMcpServer
        {
            Id = "planning",
            DisplayName = "Planning",
            Url = "https://planning.example.test/mcp",
            AuthMode = NhAssistantMcpAuthModes.None,
            IsEnabled = true,
            CreatedAt = now,
            UpdatedAt = now
        });
        context.AgentMcpServers.Add(new AssistantAgentMcpServer { AgentId = AgentId, McpServerId = "planning" });
        await context.SaveChangesAsync();
    }

    private static string[] ToolNames(NhAiRecordedChatRequest request)
    {
        return request.Options?.Tools?.Select(tool => tool.Name).Order(StringComparer.Ordinal).ToArray() ?? [];
    }

    private sealed class CountingToolSource : INhAssistantMcpToolSource
    {
        public int Calls { get; private set; }

        public Task<NhAssistantMcpToolSet> GetToolsAsync(NhAssistantMcpToolRequest request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(NhAssistantMcpToolSet.Empty);
        }
    }
}
