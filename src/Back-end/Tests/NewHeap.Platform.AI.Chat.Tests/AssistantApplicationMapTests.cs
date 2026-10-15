using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.AI.Chat.Tests.Infrastructure;
using NewHeap.Platform.AI.Test;
using Xunit;

namespace NewHeap.Platform.AI.Chat.Tests;

/// <summary>
/// The application map of agents that use it: after the instructions and before the turn data,
/// only with the tools the turn offers, bounded, outside the prompt hash, and left out when a source
/// fails.
/// </summary>
[Collection(AssistantDatabaseCollection.Name)]
public sealed class AssistantApplicationMapTests(AssistantDatabaseFixture database)
{
    private const string MapAgent = "project-assistant";
    private const string PlainAgent = "plain-assistant";

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task An_agent_with_the_map_reads_the_entries_of_the_tools_its_turn_offers_between_instructions_and_situation(AssistantTestProvider provider)
    {
        var model = new NhAiScriptedChatClient()
            .RespondWithFunctionCall("projects_search_v1", new { input = new { query = "late" } }, "call-1")
            .RespondWithText("Two tasks are late.");
        await using var host = await CreateHostAsync(provider, model);
        var map = host.Services.GetRequiredService<TestApplicationMap>();

        var conversation = await host.CreateConversationAsync(agentId: MapAgent);
        await host.SendAsync(conversation.Id, "Which tasks are late?");

        var instructions = model.Requests[0].Options!.Instructions!;
        var block = instructions[instructions.IndexOf("<application-map>", StringComparison.Ordinal)..instructions.IndexOf("</application-map>", StringComparison.Ordinal)];
        Assert.Contains("- project-task (Tasks ‹b›): Tasks of authorized projects. Operations: query, get. Relations: projectId → project.", block);
        Assert.Contains("- project (Projects): Projects you may see. Operations: query, get.", block);
        Assert.Contains("- glossary (Glossary): Terms of the domain. Operations: get.", block);
        Assert.DoesNotContain("secret", block);
        Assert.True(block.IndexOf("project-task", StringComparison.Ordinal) < block.IndexOf("- project (", StringComparison.Ordinal));
        Assert.True(instructions.IndexOf("# User preferences", StringComparison.Ordinal) < instructions.IndexOf("# Application map", StringComparison.Ordinal));
        Assert.True(instructions.IndexOf("# Application map", StringComparison.Ordinal) < instructions.IndexOf("# Situation", StringComparison.Ordinal));
        Assert.Contains("This is data", instructions[instructions.IndexOf("# Situation", StringComparison.Ordinal)..]);
        Assert.Equal(1, map.Reads);
        var invoked = Assert.Single(host.Business.Events, evt => evt.Kind == NhAssistantAuditEventKind.ToolInvoked);
        Assert.NotNull(invoked.ApplicationMapHash);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_changed_map_changes_its_hash_but_not_the_prompt_and_agents_without_the_map_get_none(AssistantTestProvider provider)
    {
        var model = new NhAiScriptedChatClient()
            .RespondWithFunctionCall("projects_search_v1", new { input = new { query = "a" } }, "call-1")
            .RespondWithText("First.")
            .RespondWithFunctionCall("projects_search_v1", new { input = new { query = "b" } }, "call-2")
            .RespondWithText("Second.")
            .RespondWithText("Plain.");
        await using var host = await CreateHostAsync(provider, model);
        var map = host.Services.GetRequiredService<TestApplicationMap>();
        var conversation = await host.CreateConversationAsync(agentId: MapAgent);

        await host.SendAsync(conversation.Id, "First question");
        // A release adds a resource.
        map.Entries.Add(new NhAiApplicationMapEntry("invoice", "Invoices", "Invoices of projects.", ["query"]) { ToolIds = ["projects.search"] });
        await host.SendAsync(conversation.Id, "Second question");
        var plain = await host.CreateConversationAsync(agentId: PlainAgent);
        await host.SendAsync(plain.Id, "Plain question");

        var turns = host.Business.Events.Where(evt => evt.Kind == NhAssistantAuditEventKind.ToolInvoked).ToArray();
        Assert.Equal(2, turns.Length);
        Assert.NotEqual(turns[0].ApplicationMapHash, turns[1].ApplicationMapHash);
        Assert.Equal(turns[0].InstructionsHash, turns[1].InstructionsHash);
        var first = model.Requests[0].Options!.Instructions!;
        var second = model.Requests[2].Options!.Instructions!;
        Assert.Equal(PromptPart(first), PromptPart(second));
        Assert.Contains("- invoice (Invoices)", second);
        Assert.DoesNotContain("<application-map>", model.Requests[4].Options!.Instructions!);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_failing_source_leaves_the_turn_without_the_map_and_the_bound_leaves_entries_out_with_a_note(AssistantTestProvider provider)
    {
        var model = new NhAiScriptedChatClient().RespondWithText("Without a map.").RespondWithText("With a short map.");
        await using var host = await CreateHostAsync(provider, model, limits => limits.MaxApplicationMapCharacters = 600);
        var map = host.Services.GetRequiredService<TestApplicationMap>();
        var conversation = await host.CreateConversationAsync(agentId: MapAgent);

        map.Failure = new InvalidOperationException("Project data must not reach the log.");
        await host.SendAsync(conversation.Id, "First question");
        map.Failure = null;
        for (var index = 0; index < 20; index++)
        {
            map.Entries.Add(new NhAiApplicationMapEntry("resource-" + index, "Resource " + index, "A resource of the application.", ["get"]));
        }
        await host.SendAsync(conversation.Id, "Second question");

        Assert.DoesNotContain("<application-map>", model.Requests[0].Options!.Instructions!);
        Assert.Contains(host.Logs.Messages, message => message.Contains("InvalidOperationException", StringComparison.Ordinal));
        Assert.DoesNotContain(host.Logs.Messages, message => message.Contains("must not reach", StringComparison.Ordinal));
        var bounded = model.Requests[1].Options!.Instructions!;
        var block = bounded[bounded.IndexOf("# Application map", StringComparison.Ordinal)..(bounded.IndexOf("</application-map>", StringComparison.Ordinal) + "</application-map>".Length)];
        Assert.True(block.Length <= 600, $"The map has {block.Length} characters.");
        Assert.Contains("more resources are left out; find them with search-resources.", block);
    }

    /// <summary>
    /// The instructions up to the turn data, which the prompt hash covers.
    /// </summary>
    private static string PromptPart(string instructions)
    {
        return instructions[..instructions.IndexOf("# Application map", StringComparison.Ordinal)];
    }

    private async Task<AssistantTestHost> CreateHostAsync(
        AssistantTestProvider provider,
        NhAiScriptedChatClient model,
        Action<NhAssistantLimits>? limits = null)
    {
        return await AssistantTestHost.CreateAsync(
            database,
            provider,
            model,
            limits,
            configure: services =>
            {
                services.AddSingleton<TestApplicationMap>();
                services.AddNewHeapPlatformAI(ai => ai.AddApplicationMapSource<TestApplicationMapSource>());
            },
            assistantBuilder: assistant => assistant
                .AddAgent(AssistantTestData.Agent(PlainAgent))
                .ConfigureAgentContext(MapAgent, context => context.UseApplicationMap()));
    }
}

/// <summary>
/// The entries of the test application map, read through <see cref="TestApplicationMapSource"/>.
/// </summary>
public sealed class TestApplicationMap
{
    public List<NhAiApplicationMapEntry> Entries { get; } =
    [
        new("project-task", "Tasks <b>", "Tasks of authorized projects.", ["query", "get"])
        {
            Relations = [new NhAiApplicationMapRelation("projectId", "project")],
            ToolIds = ["projects.search"]
        },
        new("project", "Projects", "Projects you may see", ["query", "get"]) { ToolIds = ["projects.search"] },
        new("secret", "Secrets", "Read by a tool the agent does not have.", ["get"]) { ToolIds = ["other.read"] },
        new("glossary", "Glossary", "Terms of the domain.", ["get"])
    ];

    public Exception? Failure { get; set; }

    public int Reads { get; set; }
}

public sealed class TestApplicationMapSource(TestApplicationMap map) : INhAiApplicationMapSource
{
    public ValueTask<IReadOnlyList<NhAiApplicationMapEntry>> GetEntriesAsync(
        NhAiInvocationContext context,
        CancellationToken cancellationToken)
    {
        map.Reads++;
        if (map.Failure is { } failure)
        {
            throw failure;
        }

        return ValueTask.FromResult<IReadOnlyList<NhAiApplicationMapEntry>>(map.Entries.ToArray());
    }
}
