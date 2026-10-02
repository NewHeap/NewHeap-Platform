using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using NewHeap.Platform.AI.Chat.AspNet;
using NewHeap.Platform.AI.Chat.AspNet.Mcp;
using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.AI.Chat.Runtime;
using NewHeap.Platform.AI.Chat.Tests.Infrastructure;
using NewHeap.Platform.AI.Test;
using Xunit;

namespace NewHeap.Platform.AI.Chat.Tests;

/// <summary>
/// The assistant context reaches a reviewed MCP tool as request metadata over the official MCP
/// transport, and the conversation snapshot refuses foreign, stale and incomplete state.
/// </summary>
[Collection(AssistantDatabaseCollection.Name)]
public sealed class AssistantMcpContextTests(AssistantDatabaseFixture database)
{
    private const string ResearchUrl = "http://localhost/research/mcp";
    private const string ResearchFunction = "mcp_research_research_ask";
    private const string AgentActorId = "assistant-agent:project-assistant";

    [Fact]
    public async Task A_reviewed_tool_receives_the_assistant_context_and_a_bounded_snapshot()
    {
        var model = new NhAiScriptedChatClient()
            .RespondWithText("Alpha migrates the CRM.")
            .RespondWithFunctionCall(ResearchFunction, new { question = "Which risks block Alpha?" })
            .RespondWithText("Research is queued.");
        var research = new ResearchServer();
        await using var host = await CreateHostAsync(model, research);
        await ConnectResearchServerAsync(host, ResearchUrl, NhAssistantMcpToolEffects.ReadOnly);
        var conversation = await host.CreateConversationAsync();
        await host.SendAsync(conversation.Id, "What is the scope of Alpha?");

        var turn = await host.SendAsync(conversation.Id, "Ask research which risks block Alpha.");

        Assert.Equal(NhAssistantToolCallStatuses.Succeeded, turn.Single<NhAssistantToolCompletedEvent>().Status);
        // The model saw only the remote schema and the remote server received only its arguments.
        var offered = Assert.Single(model.Requests[1].Options!.Tools!, tool => tool.Name == ResearchFunction);
        Assert.Equal(["question"], Assert.IsAssignableFrom<AIFunctionDeclaration>(offered).JsonSchema.GetProperty("properties").EnumerateObject().Select(item => item.Name));
        var call = Assert.Single(research.Calls);
        Assert.Equal(["question"], call.Arguments.Keys);

        var context = call.Context!;
        var started = turn.Single<NhAssistantTurnStartedEvent>();
        Assert.Equal(NhAssistantMcpContext.Version, context["version"]!.GetValue<int>());
        Assert.Equal(turn.Single<NhAssistantToolStartedEvent>().InvocationId.ToString(), context["invocation"]!["invocationId"]!.GetValue<string>());
        Assert.Equal(AgentActorId, context["invocation"]!["actor"]!["id"]!.GetValue<string>());
        Assert.Equal("agent", context["invocation"]!["actor"]!["kind"]!.GetValue<string>());
        Assert.Equal(AssistantTestHost.UserId, context["invocation"]!["accountableOwnerId"]!.GetValue<string>());
        Assert.Equal(started.TurnId.ToString(), context["invocation"]!["runId"]!.GetValue<string>());
        Assert.Equal(conversation.Id.ToString(), context["conversation"]!["id"]!.GetValue<string>());
        Assert.Equal(started.TurnId.ToString(), context["conversation"]!["turnId"]!.GetValue<string>());
        Assert.Equal(AssistantTestHost.UserId, context["conversation"]!["ownerActorId"]!.GetValue<string>());

        var snapshot = context["snapshot"]!;
        Assert.Equal("What is the scope of Alpha?", snapshot["firstQuestion"]!["text"]!.GetValue<string>());
        Assert.Equal(AssistantTestHost.UserId, snapshot["firstQuestion"]!["authorActorId"]!.GetValue<string>());
        Assert.Equal(
            ["assistant:Alpha migrates the CRM.", "user:Ask research which risks block Alpha."],
            snapshot["recentMessages"]!.AsArray().Select(item => $"{item!["role"]}:{item["text"]}"));
        Assert.Equal(0, snapshot["omittedMessages"]!.GetValue<int>());
        // Instructions, tool calls and the running answer never leave the assistant.
        Assert.DoesNotContain("Research is queued", context.ToJsonString(), StringComparison.Ordinal);
        Assert.DoesNotContain("tool-call", context.ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_server_whose_url_differs_from_the_reviewed_binding_receives_no_context()
    {
        var model = new NhAiScriptedChatClient()
            .RespondWithFunctionCall(ResearchFunction, new { question = "Which risks block Alpha?" })
            .RespondWithText("Research is queued.");
        var research = new ResearchServer();
        const string otherUrl = "http://localhost/elsewhere/mcp";
        await using var host = await CreateHostAsync(model, research, otherUrl);
        await ConnectResearchServerAsync(host, otherUrl, NhAssistantMcpToolEffects.ReadOnly);
        var conversation = await host.CreateConversationAsync();

        var turn = await host.SendAsync(conversation.Id, "Ask research which risks block Alpha.");

        Assert.Equal(NhAssistantToolCallStatuses.Succeeded, turn.Single<NhAssistantToolCompletedEvent>().Status);
        Assert.Null(Assert.Single(research.Calls).Context);
    }

    [Fact]
    public async Task A_resumed_turn_binds_the_approval_and_leaves_out_output_written_before_the_pause()
    {
        var model = new NhAiScriptedChatClient()
            .RespondWithFunctionCall(ResearchFunction, new { question = "Start a risk review for Alpha." })
            .RespondWithText("The review started.");
        var research = new ResearchServer();
        await using var host = await CreateHostAsync(model, research);
        await ConnectResearchServerAsync(host, ResearchUrl, NhAssistantMcpToolEffects.Mutation);
        var conversation = await host.CreateConversationAsync();

        var paused = await host.SendAsync(conversation.Id, "Start a risk review for Alpha.");
        var approval = paused.Single<NhAssistantApprovalRequiredEvent>().Approval;
        Assert.Empty(research.Calls);
        // Text the paused turn already wrote is still incomplete output of the running turn.
        var pausedMessageId = paused.Single<NhAssistantTurnStartedEvent>().AssistantMessageId;
        await host.Services.GetRequiredService<NhAssistantDbContextFactory>().CreateDbContext().UsingAsync(async context =>
        {
            var message = await context.Messages.FindAsync(pausedMessageId);
            message!.PartsJson = NhAssistantContent.SerializeParts([new NhAssistantStoredPart(NhAssistantStoredPart.TextType, Text: "I will start the review once you approve.")]);
            await context.SaveChangesAsync();
        });

        var resumed = await host.DecideAsync(conversation.Id, approval, approve: true);

        Assert.Equal(NhAssistantToolCallStatuses.Succeeded, resumed.Single<NhAssistantToolCompletedEvent>().Status);
        var context = Assert.Single(research.Calls).Context!;
        Assert.Equal(approval.ApprovalId.ToString(), context["invocation"]!["approvalId"]!.GetValue<string>());
        Assert.Equal(approval.ProposalId.ToString(), context["invocation"]!["proposalId"]!.GetValue<string>());
        Assert.Equal(paused.Single<NhAssistantTurnStartedEvent>().TurnId.ToString(), context["conversation"]!["turnId"]!.GetValue<string>());
        Assert.Empty(context["snapshot"]!["recentMessages"]!.AsArray());
        Assert.Equal("Start a risk review for Alpha.", context["snapshot"]!["firstQuestion"]!["text"]!.GetValue<string>());
        Assert.DoesNotContain("once you approve", context.ToJsonString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task The_snapshot_is_only_available_inside_the_running_call_of_its_turn(AssistantTestProvider provider)
    {
        await using var host = await AssistantTestHost.CreateAsync(database, provider, new NhAiScriptedChatClient());
        var state = await SeedAsync(host, activeTurn: true);

        var outside = await SnapshotAsync(host, state.Invocation);
        using (state.Enter())
        {
            var foreignOwner = await SnapshotAsync(host, state.Invocation with { AccountableOwnerId = "user-2" });
            var foreignCall = await SnapshotAsync(host, state.Invocation with { InvocationId = Guid.NewGuid() });
            var humanActor = await SnapshotAsync(host, state.Invocation with { ActorKind = NhAiActorKind.Human });
            var otherRun = await SnapshotAsync(host, state.Invocation with { RunId = Guid.NewGuid().ToString() });

            Assert.Equal(NhAssistantSnapshotCodes.Forbidden, FirstCode(foreignOwner));
            Assert.Equal(NhAssistantSnapshotCodes.Forbidden, FirstCode(foreignCall));
            Assert.Equal(NhAssistantSnapshotCodes.Forbidden, FirstCode(humanActor));
            Assert.Equal(NhAssistantSnapshotCodes.Forbidden, FirstCode(otherRun));
            Assert.True((await SnapshotAsync(host, state.Invocation)).Success);
        }

        Assert.Equal(NhAssistantSnapshotCodes.Unavailable, FirstCode(outside));
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_turn_of_another_person_or_a_stale_turn_gets_no_snapshot(AssistantTestProvider provider)
    {
        await using var host = await AssistantTestHost.CreateAsync(database, provider, new NhAiScriptedChatClient());
        // The turn claims an actor that is neither the owner nor a participant.
        var stranger = await SeedAsync(host, activeTurn: true, turnActorId: "user-2");
        var ended = await SeedAsync(host, activeTurn: false);
        var replaced = await SeedAsync(host, activeTurn: true);
        await host.Services.GetRequiredService<NhAssistantDbContextFactory>().CreateDbContext().UsingAsync(async context =>
        {
            var conversation = await context.Conversations.FindAsync(replaced.ConversationId);
            conversation!.ActiveTurnId = Guid.NewGuid();
            await context.SaveChangesAsync();
        });

        Assert.Equal(NhAssistantSnapshotCodes.Forbidden, FirstCode(await stranger.SnapshotInsideAsync(host)));
        Assert.Equal(NhAssistantSnapshotCodes.Stale, FirstCode(await ended.SnapshotInsideAsync(host)));
        Assert.Equal(NhAssistantSnapshotCodes.Stale, FirstCode(await replaced.SnapshotInsideAsync(host)));
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task Incomplete_output_is_left_out_and_oversized_content_is_cut_and_reported(AssistantTestProvider provider)
    {
        await using var host = await AssistantTestHost.CreateAsync(database, provider, new NhAiScriptedChatClient());
        var longQuestion = string.Concat(Enumerable.Repeat("Alpha 🚀 ", 200));
        var older = Enumerable.Range(1, 6)
            .SelectMany(index => new[]
            {
                (NhAssistantMessageRoles.User, $"Question {index}"),
                (NhAssistantMessageRoles.Assistant, $"Answer {index}")
            })
            .ToArray();
        var state = await SeedAsync(host, activeTurn: true, firstQuestion: longQuestion, history: older);
        var request = new NhAssistantConversationSnapshotRequest { MaxRecentMessages = 3, MaxBytes = 1_024, MaxMessageBytes = 256 };

        var result = await state.SnapshotInsideAsync(host, request);

        Assert.True(result.Success);
        var snapshot = result.Data!;
        Assert.True(snapshot.FirstQuestion!.Truncated);
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(snapshot.FirstQuestion.Text) <= 256);
        Assert.False(char.IsHighSurrogate(snapshot.FirstQuestion.Text[^1]));
        // The running turn's question is complete; its partial answer is not.
        Assert.Equal(["Question 6", "Answer 6", "Current question"], snapshot.RecentMessages.Select(message => message.Text));
        Assert.DoesNotContain(snapshot.RecentMessages, message => message.Text.Contains("partial", StringComparison.Ordinal));
        Assert.Equal(10, snapshot.OmittedMessages);
        var omittedHistory = older.Take(10).Sum(item => System.Text.Encoding.UTF8.GetByteCount(item.Item2));
        var cutFromQuestion = System.Text.Encoding.UTF8.GetByteCount(longQuestion) - System.Text.Encoding.UTF8.GetByteCount(snapshot.FirstQuestion.Text);
        Assert.Equal(omittedHistory + cutFromQuestion, snapshot.OmittedBytes);
    }

    [Fact]
    public void Snapshot_bounds_outside_the_hard_limits_are_refused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new NhAssistantConversationSnapshotRequest { MaxRecentMessages = 21 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new NhAssistantConversationSnapshotRequest { MaxBytes = 70_000 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new NhAssistantConversationSnapshotRequest { MaxBytes = 1_024, MaxMessageBytes = 2_048 }.Validate());
    }

    private static string? FirstCode(NewHeap.Platform.Common.Models.TaskResult result)
    {
        return result.GetResultItems().Select(item => item.Name).FirstOrDefault(name => !string.IsNullOrEmpty(name));
    }

    private static async Task<NewHeap.Platform.Common.Models.TaskResult<NhAssistantConversationSnapshot>> SnapshotAsync(
        AssistantTestHost host,
        NhAiInvocationContext invocation,
        NhAssistantConversationSnapshotRequest? request = null)
    {
        await using var scope = host.Services.CreateAsyncScope();
        return await scope.ServiceProvider
            .GetRequiredService<INhAssistantConversationSnapshotProvider>()
            .GetSnapshotAsync(invocation, request);
    }

    private static async Task<SeededTurn> SeedAsync(
        AssistantTestHost host,
        bool activeTurn,
        string turnActorId = AssistantTestHost.UserId,
        string firstQuestion = "First question",
        IReadOnlyList<(string Role, string Text)>? history = null)
    {
        var conversation = await host.CreateConversationAsync();
        var turnId = Guid.NewGuid();
        var sequence = 0;
        var messages = new List<AssistantMessage> { Message(conversation.Id, Guid.NewGuid(), ++sequence, NhAssistantMessageRoles.User, firstQuestion) };
        foreach (var (role, text) in history ?? [])
        {
            messages.Add(Message(conversation.Id, Guid.NewGuid(), ++sequence, role, text));
        }
        messages.Add(Message(conversation.Id, turnId, ++sequence, NhAssistantMessageRoles.User, "Current question"));
        messages.Add(Message(conversation.Id, turnId, ++sequence, NhAssistantMessageRoles.Assistant, "A partial answer"));
        await host.Services.GetRequiredService<NhAssistantDbContextFactory>().CreateDbContext().UsingAsync(async context =>
        {
            context.Messages.AddRange(messages);
            var stored = await context.Conversations.FindAsync(conversation.Id);
            stored!.Status = activeTurn ? NhAssistantConversationStatuses.Running : NhAssistantConversationStatuses.Idle;
            stored.ActiveTurnId = activeTurn ? turnId : null;
            stored.ActiveActorId = activeTurn ? turnActorId : null;
            await context.SaveChangesAsync();
        });

        var callId = Guid.NewGuid();
        var turn = new NhAssistantTurnScope
        {
            ConversationId = conversation.Id,
            TurnId = turnId,
            Agent = AssistantTestData.Agent(),
            OwnerActorId = turnActorId,
            ConversationOwnerActorId = conversation.OwnerActorId,
            AccountableOwnerId = turnActorId,
            ModelProfileName = "assistant-test",
            PromptVersion = "1",
            PromptHash = "hash",
            Deadline = DateTimeOffset.UtcNow.AddMinutes(5),
            Limits = new NhAssistantLimits()
        };
        var call = new NhAssistantToolCallScope
        {
            InvocationId = callId,
            Descriptor = new NhAiToolDescriptor(
                "mcp.research.research-ask",
                1,
                "Asks the research service.",
                typeof(object),
                typeof(object),
                NhAiToolEffect.ReadOnly,
                NhAiToolExposure.Agent,
                true,
                ["app.assistant.access"]),
            IdempotencyKey = "key"
        };
        var invocation = new NhAiInvocationContext(AgentActorId, "assistant", new Dictionary<string, string>())
        {
            InvocationId = callId,
            ActorKind = NhAiActorKind.Agent,
            AccountableOwnerId = turnActorId,
            RunId = turnId.ToString()
        };
        return new SeededTurn(conversation.Id, turn, call, invocation);
    }

    private static AssistantMessage Message(Guid conversationId, Guid turnId, int sequence, string role, string text)
    {
        return new AssistantMessage
        {
            Id = Guid.NewGuid(),
            ConversationId = conversationId,
            TurnId = turnId,
            Role = role,
            Sequence = sequence,
            PartsJson = NhAssistantContent.SerializeParts([new NhAssistantStoredPart(NhAssistantStoredPart.TextType, Text: text)]),
            CreatedAt = DateTimeOffset.UtcNow.AddSeconds(sequence)
        };
    }

    private sealed record SeededTurn(
        Guid ConversationId,
        NhAssistantTurnScope Turn,
        NhAssistantToolCallScope Call,
        NhAiInvocationContext Invocation)
    {
        public IDisposable Enter()
        {
            var turn = NhAssistantExecutionScope.EnterTurn(Turn);
            var call = NhAssistantExecutionScope.EnterCall(Call);
            return new Exit(() =>
            {
                call.Dispose();
                turn.Dispose();
            });
        }

        public async Task<NewHeap.Platform.Common.Models.TaskResult<NhAssistantConversationSnapshot>> SnapshotInsideAsync(
            AssistantTestHost host,
            NhAssistantConversationSnapshotRequest? request = null)
        {
            using var _ = Enter();
            return await SnapshotAsync(host, Invocation, request);
        }

        private sealed class Exit(Action exit) : IDisposable
        {
            public void Dispose() => exit();
        }
    }

    private Task<AssistantTestHost> CreateHostAsync(
        NhAiScriptedChatClient model,
        ResearchServer research,
        string serverUrl = ResearchUrl)
    {
        var servers = new InMemoryMcpServers();
        servers.Register(serverUrl, research.Handlers);
        return AssistantTestHost.CreateAsync(
            database,
            AssistantTestProvider.PostgreSql,
            model,
            configure: services => services.Replace(ServiceDescriptor.Singleton<INhAssistantMcpClientFactory>(servers)),
            assistantBuilder: assistant => assistant
                .ConfigureMcp(mcp => mcp.RequireHttps = true)
                .AddMcpContextBinding(new NhAssistantMcpContextBinding("research", ResearchUrl, "research-ask")
                {
                    Snapshot = new NhAssistantConversationSnapshotRequest { MaxRecentMessages = 4, MaxBytes = 2_048, MaxMessageBytes = 512 }
                }));
    }

    private static async Task ConnectResearchServerAsync(AssistantTestHost host, string url, string effect)
    {
        await using (var scope = host.Services.CreateAsyncScope())
        {
            AssistantTestHost.EnterUser(scope.ServiceProvider, "admin-1");
            var administration = scope.ServiceProvider.GetRequiredService<NhAssistantMcpAdministration>();
            Assert.True((await administration.CreateAsync(
                new NhAssistantMcpServerInput("research", "Research", url, NhAssistantMcpAuthModes.None, null, null, null, true),
                "admin-1",
                CancellationToken.None)).Success);
            Assert.True((await administration.SyncAsync("research", "admin-1", CancellationToken.None)).Success);
            Assert.True((await administration.UpdateToolAsync("research", "research-ask", true, effect, null, "admin-1", CancellationToken.None)).Success);
        }
        await using var agentScope = host.Services.CreateAsyncScope();
        var agents = agentScope.ServiceProvider.GetRequiredService<NhAssistantAgentAdministration>();
        var catalog = agentScope.ServiceProvider.GetRequiredService<NhAssistantAgentCatalog>();
        var agent = (await catalog.FindAsync("project-assistant", includeDisabled: true, CancellationToken.None))!;
        Assert.True((await agents.UpdateAsync(
            new NhAssistantAgentInput(
                agent.Id,
                agent.Definition.DisplayNameKey,
                agent.Definition.DescriptionKey,
                agent.Definition.Instructions.Content,
                agent.Definition.ToolSelectors,
                ["research"],
                agent.Definition.RequiredPolicy,
                agent.Definition.Autonomy,
                true),
            agent.Definition.Version,
            "admin-1",
            CancellationToken.None)).Success);
    }

    /// <summary>
    /// A research server shaped like a version 2 contract: the model-facing schema only has the
    /// question; the host context arrives as request metadata.
    /// </summary>
    private sealed class ResearchServer
    {
        public ConcurrentQueue<(JsonObject? Context, IReadOnlyDictionary<string, JsonElement> Arguments)> Calls { get; } = new();

        public McpServerHandlers Handlers => new()
        {
            ListToolsHandler = (_, _) => ValueTask.FromResult(new ListToolsResult
            {
                Tools =
                [
                    new Tool
                    {
                        Name = "research-ask",
                        Description = "Asks the research service a question.",
                        InputSchema = JsonSerializer.SerializeToElement(new
                        {
                            type = "object",
                            properties = new { question = new { type = "string" } },
                            required = new[] { "question" }
                        })
                    }
                ]
            }),
            CallToolHandler = (request, _) =>
            {
                Calls.Enqueue((
                    request.Params?.Meta?[NhAssistantMcpContext.MetaKey]?.DeepClone() as JsonObject,
                    new Dictionary<string, JsonElement>(request.Params?.Arguments ?? new Dictionary<string, JsonElement>())));
                return ValueTask.FromResult(new CallToolResult { Content = [new TextContentBlock { Text = "Research queued." }] });
            }
        };
    }
}
