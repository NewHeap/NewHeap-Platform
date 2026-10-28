using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.AI.AspNet;
using NewHeap.Platform.AI.Chat.Tests.Infrastructure;
using NewHeap.Platform.AI.Test;
using Xunit;

namespace NewHeap.Platform.AI.Chat.Tests;

/// <summary>
/// Skills in a person's chat (LS20): <c>run.SkillsInChat()</c> offers <c>use_skill</c> in people's
/// chats with the agent, without triage; the skill's instructions, tools, profile and token ceiling
/// apply within the person's rights, and the agent's message of each turn records the skill. An agent
/// without the opt-in keeps its own tools in the chat. Proved on both relational providers.
/// </summary>
public sealed class AssistantChatSkillTests(AssistantDatabaseFixture database) : IClassFixture<AssistantDatabaseFixture>
{
    private const string AgentId = "project-assistant";
    private const string PlainAgentId = "plain-assistant";
    private const string Risk = "risk";
    private const string Notes = "notes";
    private const string Tight = "tight";
    private const string RiskInstructions = "Judge whether the project misses its deadline.";
    private const string SearchFunction = "projects_search_v1";
    private const string ChangeStatusFunction = "projects_change_status_v1";

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task Use_skill_is_offered_in_a_chat_only_with_the_opt_in_and_narrows_the_tools_without_triage(AssistantTestProvider provider)
    {
        var models = new SkillModels();
        models.Default.RespondWithFunctionCall("use_skill", new { skill = Risk, reason = "The person asks about the deadline." }, "call-risk");
        models.Skill.RespondWithText("The deadline is safe.");
        models.Plain.RespondWithText("I can help with projects.");
        await using var host = await CreateHostAsync(provider, models);
        var opted = await host.CreateConversationAsync(agentId: AgentId);
        var plain = await host.CreateConversationAsync(agentId: PlainAgentId);

        var inSkill = await host.SendAsync(opted.Id, "Will we make the deadline?");
        var withoutSkills = await host.SendAsync(plain.Id, "What can you do?");

        Assert.Equal((NhAssistantTurnStatuses.Completed, (string?)null), Outcome(inSkill));
        Assert.Equal((NhAssistantTurnStatuses.Completed, (string?)null), Outcome(withoutSkills));

        // The switch itself is a successful call, not a failed tool call.
        var switched = inSkill.Single<Runtime.NhAssistantToolCompletedEvent>();
        Assert.Equal((NhAssistantToolCallStatuses.Succeeded, (string?)null), (switched.Status, switched.ResultCode));
        var first = models.Default.Requests[0];
        Assert.Contains("use_skill", ToolNames(first));
        Assert.Contains(SearchFunction, ToolNames(first));
        Assert.Contains(ChangeStatusFunction, ToolNames(first));

        // The skill works with its own profile, instructions and only its tools.
        var skill = Assert.Single(models.Skill.Requests);
        Assert.Contains(RiskInstructions, skill.Options!.Instructions);
        Assert.Contains(ChangeStatusFunction, ToolNames(skill));
        Assert.DoesNotContain(SearchFunction, ToolNames(skill));
        Assert.Contains(skill.Messages, message => message.Text.Contains("You now work in skill 'risk'", StringComparison.Ordinal));

        // The agent without the opt-in offers no skills in its chat, and no chat runs triage.
        var other = Assert.Single(models.Plain.Requests);
        Assert.DoesNotContain("use_skill", ToolNames(other));
        Assert.Contains(SearchFunction, ToolNames(other));
        Assert.Empty(models.Triage.Requests);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task The_agents_message_records_the_skill_of_each_turn_and_the_next_turn_starts_without_one(AssistantTestProvider provider)
    {
        var models = new SkillModels();
        models.Default
            .RespondWithFunctionCall("use_skill", new { skill = Notes, reason = "A note is needed." }, "call-notes")
            .RespondWithText("I noted it.")
            .RespondWithText("Hello again.");
        await using var host = await CreateHostAsync(provider, models);
        var conversation = await host.CreateConversationAsync(agentId: AgentId);

        await host.SendAsync(conversation.Id, "Write a note about the launch.");
        await host.SendAsync(conversation.Id, "Hello.");

        var view = await host.ReadViewAsync(conversation.Id);
        var agentMessages = view.Messages.Where(message => message.Role == NhAssistantMessageRoles.Assistant).ToArray();
        Assert.Equal(2, agentMessages.Length);
        Assert.Equal(Notes, agentMessages[0].SkillId);
        Assert.Null(agentMessages[1].SkillId);

        // The skill's own tools in the turn that loaded it, the agent's tools and use_skill in the next.
        Assert.Equal(3, models.Default.Requests.Count);
        Assert.Contains(SearchFunction, ToolNames(models.Default.Requests[1]));
        Assert.DoesNotContain(ChangeStatusFunction, ToolNames(models.Default.Requests[1]));
        Assert.Contains("use_skill", ToolNames(models.Default.Requests[2]));
        Assert.Contains(ChangeStatusFunction, ToolNames(models.Default.Requests[2]));
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_skills_token_ceiling_applies_in_a_chat(AssistantTestProvider provider)
    {
        var models = new SkillModels();
        models.Default
            .RespondWithFunctionCall("use_skill", new { skill = Tight, reason = "The person asks for a search." }, "call-tight")
            .RespondWithFunctionCall(SearchFunction, new { input = new { query = "launch", limit = 5 } }, "call-search");
        await using var host = await CreateHostAsync(provider, models);
        var conversation = await host.CreateConversationAsync(agentId: AgentId);

        var turn = await host.SendAsync(conversation.Id, "Search the launch projects.");

        // The skill allows one token: its tool call is refused before it runs and the turn stops there.
        var completed = turn.Single<Runtime.NhAssistantTurnCompletedEvent>();
        Assert.Equal(NhAssistantTurnStatuses.Failed, completed.Status);
        Assert.Equal(NhAssistantErrorCodes.RunTokenLimit, completed.ErrorCode);
        Assert.Empty(host.Tools.Contexts);
        Assert.Equal(Tight, (await host.ReadViewAsync(conversation.Id)).Messages.Last().SkillId);
    }

    private async Task<AssistantTestHost> CreateHostAsync(AssistantTestProvider provider, SkillModels models)
    {
        return await AssistantTestHost.CreateAsync(
            database,
            provider,
            models.Default,
            configure: services =>
            {
                services.AddKeyedSingleton<IChatClient>("skill-chat-model", models.Skill);
                services.AddKeyedSingleton<IChatClient>("triage-chat-model", models.Triage);
                services.AddKeyedSingleton<IChatClient>("plain-chat-model", models.Plain);
                services.AddNewHeapPlatformAI(ai =>
                {
                    ai.AddChatProfile("skill-chat", profile => profile
                        .UseKeyedClient("skill-chat-model")
                        .RequireCapabilities(NhAiModelCapability.FunctionCalling)
                        .WithStreaming(NhAiStreamingPolicy.Allowed)
                        .PermitDataClassifications(NhAiDataClassification.Internal)
                        .PermitExecutionRegions("local")
                        .WithBudget(maxInputTokens: 8_192, maxOutputTokens: 1_024, maxCalls: 16));
                    ai.AddChatProfile("triage-chat", profile => profile
                        .UseKeyedClient("triage-chat-model")
                        .WithStreaming(NhAiStreamingPolicy.Allowed)
                        .PermitDataClassifications(NhAiDataClassification.Internal)
                        .PermitExecutionRegions("local")
                        .WithBudget(maxInputTokens: 8_192, maxOutputTokens: 256, maxCalls: 2));
                    ai.AddChatProfile("plain-chat", profile => profile
                        .UseKeyedClient("plain-chat-model")
                        .RequireCapabilities(NhAiModelCapability.FunctionCalling)
                        .WithStreaming(NhAiStreamingPolicy.Allowed)
                        .PermitDataClassifications(NhAiDataClassification.Internal)
                        .PermitExecutionRegions("local")
                        .WithBudget(maxInputTokens: 8_192, maxOutputTokens: 1_024, maxCalls: 16));
                });
            },
            assistantBuilder: assistant => assistant
                // Its own model, so each scripted model sees only its own calls.
                .AddAgent(AssistantTestData.Agent(PlainAgentId) with { ProfileName = "plain-chat" })
                .ConfigureAgent(AgentId, agent => Skills(agent).SkillsInChat())
                .ConfigureAgent(PlainAgentId, agent => Skills(agent)));
    }

    /// <summary>
    /// The same skills and triage for both agents; only the first opts in to skills in its chats.
    /// </summary>
    private static NhAssistantAgentRunBuilder Skills(NhAssistantAgentRunBuilder agent)
    {
        return agent
            .Triage(triage => triage.UseProfile("triage-chat"))
            .Skill(Risk, skill => skill
                .Description("Judges deadline risks and proposes a status change.")
                .UseProfile("skill-chat")
                .Instructions(RiskInstructions)
                .Tools("projects.change-status"))
            .Skill(Notes, skill => skill
                .Description("Writes notes about the project.")
                .Tools("projects.search"))
            .Skill(Tight, skill => skill
                .Description("Searches projects within a tiny allowance.")
                .Tools("projects.search")
                .MaxTokensPerRun(1));
    }

    private static (string Status, string? ErrorCode) Outcome(TurnResult turn)
    {
        var completed = turn.Single<Runtime.NhAssistantTurnCompletedEvent>();
        return (completed.Status, completed.ErrorCode);
    }

    private static string[] ToolNames(NhAiRecordedChatRequest request)
    {
        return request.Options?.Tools?.Select(tool => tool.Name).ToArray() ?? [];
    }

    private sealed class SkillModels
    {
        public NhAiScriptedChatClient Default { get; } = new();

        public NhAiScriptedChatClient Skill { get; } = new();

        public NhAiScriptedChatClient Triage { get; } = new();

        public NhAiScriptedChatClient Plain { get; } = new();
    }
}
