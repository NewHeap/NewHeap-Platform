using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NewHeap.Platform.AI.AspNet;
using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.AI.Chat.Runtime;
using NewHeap.Platform.AI.Chat.Tests.Infrastructure;
using NewHeap.Platform.AI.Test;
using Xunit;

namespace NewHeap.Platform.AI.Chat.Tests;

/// <summary>
/// Turns whose text does not fit the model call: the model receives the passages that fit with the
/// turn data, the block says what was left out, the audit names what was sent and the log has counts
/// only. The fit step itself is covered in <see cref="AssistantPromptFitTests"/>.
/// </summary>
public sealed class AssistantTurnDataFitTests(AssistantDatabaseFixture database) : IClassFixture<AssistantDatabaseFixture>
{
    private const string AgentId = "long-assistant";
    private const string SourceId = "test-documents";
    private const string Marker = "passage-text-8c1";
    private const string Question = "What does the roadmap say?";

    [Fact]
    public async Task A_turn_that_does_not_fit_keeps_the_highest_ranked_passages_and_the_turn_data_and_records_what_it_sent()
    {
        var model = new NhAiScriptedChatClient()
            .RespondWithFunctionCall("projects_search_v1", new { input = new { query = "roadmap" } }, "call-1")
            .RespondWithText("The roadmap is late.");
        await using var host = await CreateHostAsync(model, instructions: 28_000, retrievalBudget: 24_000);

        var conversation = await host.CreateConversationAsync(agentId: AgentId);
        var turn = await host.SendAsync(conversation.Id, Question);

        Assert.Null(turn.StartCode);
        var instructions = model.Requests[0].Options!.Instructions!;
        Assert.True(instructions.Length <= NhAssistantPromptComposer.MaxInstructionsLength);
        Assert.Contains("# Situation", instructions);
        var kept = Enumerable.Range(1, 6).Count(rank => instructions.Contains(PassageId(rank), StringComparison.Ordinal));
        Assert.InRange(kept, 1, 5);
        // The highest-ranked ones, without a gap.
        Assert.All(Enumerable.Range(1, kept), rank => Assert.Contains(PassageId(rank), instructions));
        Assert.Contains($"that did not fit and were left out: {6 - kept}.", instructions);
        // The block and the audit name what was sent, not what the resolver selected.
        var invoked = Assert.Single(host.Business.Events, evt => evt.Kind == NhAssistantAuditEventKind.ToolInvoked);
        Assert.Equal(HashInBlock(instructions), invoked.RetrievedContextHash);
        var trace = Assert.Single(host.Logs.Messages, message => message.Contains("Context for invocation", StringComparison.Ordinal));
        Assert.DoesNotContain(invoked.RetrievedContextHash!, trace);
        // The tool round reuses the same text.
        Assert.Equal(instructions, model.Requests[1].Options!.Instructions);

        var warning = Assert.Single(host.Logs.Messages, message => message.Contains("left data out of turn", StringComparison.Ordinal));
        Assert.Contains(AgentId, warning);
        Assert.Contains(invoked.TurnId.ToString(), warning);
        Assert.Contains($"{kept} passages kept, {6 - kept} dropped", warning);
        Assert.Contains("application map dropped False", warning);
        Assert.Contains("turn data dropped False", warning);
        Assert.Contains("of at most 32768", warning);
        Assert.DoesNotContain(Marker, warning);
        Assert.DoesNotContain("roadmap", warning, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("passage-", warning);
    }

    [Fact]
    public async Task A_turn_with_no_room_for_a_passage_tells_the_model_and_records_no_hash()
    {
        var model = new NhAiScriptedChatClient()
            .RespondWithFunctionCall("projects_search_v1", new { input = new { query = "roadmap" } }, "call-1")
            .RespondWithText("I could not read the documents.");
        await using var host = await CreateHostAsync(model, instructions: 30_400, retrievalBudget: 24_000);

        var conversation = await host.CreateConversationAsync(agentId: AgentId);
        await host.SendAsync(conversation.Id, Question, language: "nl");

        var instructions = model.Requests[0].Options!.Instructions!;
        Assert.DoesNotContain(Marker, instructions);
        Assert.DoesNotContain("<retrieved-context>", instructions);
        Assert.Contains("die niet pasten en zijn weggelaten: 6.", instructions);
        Assert.Contains("# Situatie", instructions);
        var invoked = Assert.Single(host.Business.Events, evt => evt.Kind == NhAssistantAuditEventKind.ToolInvoked);
        Assert.Null(invoked.RetrievedContextHash);
        Assert.Contains(host.Logs.Messages, message => message.Contains("0 passages kept, 6 dropped", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_turn_that_fits_sends_every_passage_and_logs_no_warning()
    {
        var model = new NhAiScriptedChatClient()
            .RespondWithFunctionCall("projects_search_v1", new { input = new { query = "roadmap" } }, "call-1")
            .RespondWithText("The roadmap is late.");
        await using var host = await CreateHostAsync(model, instructions: 2_000, retrievalBudget: 24_000);

        var conversation = await host.CreateConversationAsync(agentId: AgentId);
        await host.SendAsync(conversation.Id, Question);

        var instructions = model.Requests[0].Options!.Instructions!;
        Assert.All(Enumerable.Range(1, 6), rank => Assert.Contains(PassageId(rank), instructions));
        Assert.DoesNotContain("left out", instructions);
        var invoked = Assert.Single(host.Business.Events, evt => evt.Kind == NhAssistantAuditEventKind.ToolInvoked);
        Assert.Equal(HashInBlock(instructions), invoked.RetrievedContextHash);
        var trace = Assert.Single(host.Logs.Messages, message => message.Contains("Context for invocation", StringComparison.Ordinal));
        Assert.Contains(invoked.RetrievedContextHash!, trace);
        Assert.DoesNotContain(host.Logs.Messages, message => message.Contains("left data out of turn", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_note_about_missing_connections_keeps_its_room_next_to_the_passages()
    {
        var model = new NhAiScriptedChatClient().RespondWithText("Connect Planning first.");
        var longName = "Planning " + new string('p', 1_000);
        await using var host = await CreateHostAsync(
            model,
            instructions: 28_000,
            retrievalBudget: 24_000,
            services => services.Replace(ServiceDescriptor.Scoped<INhAssistantMcpToolSource>(
                _ => new ConnectionNeededToolSource(longName))));
        await AssignMcpServerAsync(host);

        var conversation = await host.CreateConversationAsync(agentId: AgentId);
        await host.SendAsync(conversation.Id, Question);

        var instructions = model.Requests[0].Options!.Instructions!;
        Assert.True(instructions.Length <= NhAssistantPromptComposer.MaxInstructionsLength);
        Assert.EndsWith(
            "When the question needs them, say that the person can connect the service with the card in the chat and ask again.",
            instructions,
            StringComparison.Ordinal);
        Assert.Contains("Not connected for this person: " + longName, instructions);
        Assert.Contains(PassageId(1), instructions);
        Assert.Contains("</retrieved-context>", instructions);
    }

    [Fact]
    public async Task Instructions_without_room_for_the_note_about_connections_leave_it_out_with_a_warning()
    {
        var model = new NhAiScriptedChatClient().RespondWithText("Connect Planning first.");
        await using var host = await CreateHostAsync(
            model,
            instructions: 31_400,
            retrievalBudget: 24_000,
            services => services.Replace(ServiceDescriptor.Scoped<INhAssistantMcpToolSource>(
                _ => new ConnectionNeededToolSource("Planning " + new string('p', 1_000)))));
        await AssignMcpServerAsync(host);

        var conversation = await host.CreateConversationAsync(agentId: AgentId);
        await host.SendAsync(conversation.Id, Question);

        var instructions = model.Requests[0].Options!.Instructions!;
        Assert.True(instructions.Length <= NhAssistantPromptComposer.MaxInstructionsLength);
        Assert.DoesNotContain("Not connected for this person", instructions);
        Assert.Contains(host.Logs.Messages, message => message.Contains("which connections are missing", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Instructions_over_the_limit_fail_the_turn_as_before_and_record_nothing_as_sent()
    {
        var model = new NhAiScriptedChatClient();
        await using var host = await CreateHostAsync(
            model,
            instructions: 32_000,
            retrievalBudget: 24_000,
            assistant: builder => builder.UseDefaultApplicationContext(Asset("The application.".PadRight(2_000, '.'))));

        var conversation = await host.CreateConversationAsync(agentId: AgentId);
        await host.SendAsync(conversation.Id, Question);

        Assert.Empty(model.Requests);
        Assert.DoesNotContain(host.Business.Events, evt => evt.RetrievedContextHash is not null);
    }

    private static string PassageId(int rank)
    {
        return "\"id\":\"passage-" + rank + "\"";
    }

    private static string HashInBlock(string instructions)
    {
        return Regex.Match(instructions, "\"contextHash\":\"([0-9a-f]{64})\"").Groups[1].Value;
    }

    private static async Task AssignMcpServerAsync(AssistantTestHost host)
    {
        var now = DateTimeOffset.UtcNow;
        await using (var scope = host.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<NhAssistantAgentCatalog>()
                .FindAsync(AgentId, includeDisabled: true, CancellationToken.None);
        }

        await host.Services.GetRequiredService<NhAssistantDbContextFactory>()
            .CreateDbContext()
            .UsingAsync(async context =>
            {
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
            });
    }

    private async Task<AssistantTestHost> CreateHostAsync(
        NhAiScriptedChatClient model,
        int instructions,
        int retrievalBudget,
        Action<IServiceCollection>? configure = null,
        Action<NhAssistantBuilder>? assistant = null)
    {
        return await AssistantTestHost.CreateAsync(
            database,
            AssistantTestProvider.PostgreSql,
            model,
            limits => limits.MaxRetrievedCharacters = retrievalBudget,
            configure: services =>
            {
                services.AddNewHeapPlatformAI(ai => ai
                    .AddContextSource<RankedPassageSource>()
                    .AddContextContributor<TestDivisionContributor>()
                    .UseContextAuthorizationPolicy<TestDocumentPolicy>());
                configure?.Invoke(services);
            },
            assistantBuilder: builder =>
            {
                builder
                    .AddAgent(AssistantTestData.Agent(AgentId) with { Instructions = Asset("Help with projects.".PadRight(instructions, '.')) })
                    .ConfigureAgentContext(AgentId, context => context.UseRetrievedContext(retrieval => retrieval
                        .Sources(SourceId)
                        .MaxItems(10)
                        .MaxCharacters(retrievalBudget)));
                assistant?.Invoke(builder);
            });
    }

    private static NhAiTextAsset Asset(string content)
    {
        return NhAiTextAssetFactory.Create(
            "long-instructions",
            1,
            content,
            "embedded:NewHeap.Platform.AI.Chat.Tests/long-instructions",
            NhAiAssetRole.SystemInstructions,
            NhAiContextTrust.TrustedApplication,
            NhAiModelCapability.FunctionCalling,
            [],
            "project-context-v1",
            NhAiDataClassification.Internal,
            NhAiRetentionCategory.Evaluation,
            "project-assistant-v1");
    }

    /// <summary>
    /// Six passages of about 1,000 characters each, ranked by their ids.
    /// </summary>
    public sealed class RankedPassageSource : INhAiContextSource
    {
        public NhAiContextSourceDescriptor Descriptor { get; } = new(
            SourceId,
            "Passages of the test application.",
            NhAiDataClassification.Internal,
            10,
            2_000);

        public ValueTask<IReadOnlyList<NhAiContextItem>> RetrieveAsync(
            NhAiContextRequest request,
            CancellationToken cancellationToken = default)
        {
            var division = new NhAiExecutionScopeEntry("division", "division-a");
            var created = DateTimeOffset.UtcNow.AddDays(-1);
            var passages = Enumerable.Range(1, 6)
                .Select(rank => new NhAiContextItem(
                    "passage-" + rank,
                    "passage-" + rank,
                    Descriptor.Id,
                    "text/plain",
                    (Marker + " The roadmap passage " + rank + ".").PadRight(1_000, '.'),
                    NhAiDataClassification.Internal,
                    NhAiContextTrust.UntrustedRetrieved,
                    [division],
                    created))
                .ToArray();
            return ValueTask.FromResult<IReadOnlyList<NhAiContextItem>>(passages);
        }
    }

    /// <summary>
    /// An MCP tool source that needs the person's connection, with a name that makes its note long.
    /// </summary>
    private sealed class ConnectionNeededToolSource(string displayName) : INhAssistantMcpToolSource
    {
        public Task<NhAssistantMcpToolSet> GetToolsAsync(NhAssistantMcpToolRequest request, CancellationToken cancellationToken)
        {
            return Task.FromResult(new NhAssistantMcpToolSet([])
            {
                ConnectionsNeeded = [new NhAssistantMcpConnectionNeeded("planning", displayName, NhAssistantMcpConnectionReasons.NotConnected)]
            });
        }
    }
}
