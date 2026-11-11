using System.ComponentModel;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using NewHeap.Platform.AI.Chat.AspNet.Mcp;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.AI.Chat.Runtime;
using NewHeap.Platform.AI.Chat.Tests.Infrastructure;
using NewHeap.Platform.AI.Test;
using Xunit;

namespace NewHeap.Platform.AI.Chat.Tests;

/// <summary>
/// While the person of a chat turn answers an MCP server's request for input, the turn stands still:
/// its own timeout and the tool's stand still, its deadline moves later by that time, a heartbeat keeps
/// its claim on the conversation and the request waits the server's full wait time, at most ten minutes
/// per turn in all. The call lets its tool's concurrency slot go meanwhile, so another person's call of
/// the same tool runs, and takes one again before the answer goes back; a call that finds none in time
/// ends as <c>ai-tool-concurrency-limited</c>. The person's stop still ends the wait at once.
/// </summary>
public sealed class AssistantMcpElicitationTurnPauseTests(AssistantDatabaseFixture database) : IClassFixture<AssistantDatabaseFixture>
{
    private const string ServerId = "order-desk";
    private const string ServerUrl = "http://localhost/order-desk/mcp";
    private const string ConfirmFunction = "mcp_order-desk_confirm_order";
    private const string Colleague = "user-2";
    private const string Reason = "Customer called back";
    private static readonly DateTimeOffset Start = new(2026, 11, 11, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task A_person_who_answers_after_four_minutes_is_heard_although_the_turn_has_a_minute_and_the_turn_completes()
    {
        var clock = new AssistantManualClock(Start);
        var model = new NhAiScriptedChatClient()
            .RespondWithFunctionCall(ConfirmFunction, new { order = "A-400" })
            .RespondWithText("Order A-400 is confirmed.");
        var desk = new OrderDesk();
        // The turn's own timeout runs on the manual clock as well, so it would end the turn after a minute.
        await using var host = await CreateHostAsync(model, desk, clock, TimeSpan.FromSeconds(60), turnClock: true);
        await ConnectAsync(host, waitMinutes: null);
        var conversation = await host.CreateConversationAsync();
        DateTimeOffset? expiresAt = null;

        var turn = await host.SendWhileElicitationsWaitAsync(conversation.Id, "Confirm order A-400.", async elicitation =>
        {
            expiresAt = elicitation.ExpiresAt;

            // The person takes four minutes, four times the turn's own timeout.
            await Task.Run(() => clock.Advance(TimeSpan.FromMinutes(4)));
            await AnswerAsync(host, conversation.Id, elicitation, NhAssistantElicitationActions.Accept, Reason);
        });

        // The request shows the server's full five minutes, and the turn's minute stood still meanwhile.
        Assert.Equal(Start.AddMinutes(5), expiresAt);
        Assert.Equal(NhAssistantElicitationStatuses.Accepted, turn.Single<NhAssistantElicitationClosedEvent>().Elicitation.Status);
        Assert.Equal(["confirm:A-400:" + Reason], desk.Calls);
        Assert.Equal(NhAssistantToolCallStatuses.Succeeded, turn.Single<NhAssistantToolCompletedEvent>().Status);
        var completed = turn.Single<NhAssistantTurnCompletedEvent>();
        Assert.Equal((NhAssistantTurnStatuses.Completed, null), (completed.Status, completed.ErrorCode));
        Assert.Equal("Order A-400 is confirmed.", turn.Text);
    }

    [Fact]
    public async Task Requests_wait_no_longer_than_what_is_left_of_the_ten_minutes_a_turn_stands_still_in_all()
    {
        var clock = new AssistantManualClock(Start);
        var model = new NhAiScriptedChatClient()
            .RespondWithFunctionCall(ConfirmFunction, new { order = "A-401" }, "call-401")
            .RespondWithFunctionCall(ConfirmFunction, new { order = "A-402" }, "call-402")
            .RespondWithText("Order A-401 is confirmed; nobody confirmed A-402.");
        var desk = new OrderDesk();
        await using var host = await CreateHostAsync(model, desk, clock, TimeSpan.FromSeconds(60), turnClock: true);
        await ConnectAsync(host, waitMinutes: 10);
        var conversation = await host.CreateConversationAsync();
        var expiries = new List<DateTimeOffset>();

        var turn = await host.SendWhileElicitationsWaitAsync(conversation.Id, "Confirm orders A-401 and A-402.", async elicitation =>
        {
            expiries.Add(elicitation.ExpiresAt);
            if (expiries.Count == 1)
            {
                // The person takes seven minutes over the first request.
                await Task.Run(() => clock.Advance(TimeSpan.FromMinutes(7)));
                await AnswerAsync(host, conversation.Id, elicitation, NhAssistantElicitationActions.Accept, Reason);
                return;
            }

            // Nobody answers the second one; it ends when the turn's ten minutes are used up.
            await Task.Run(() => clock.Advance(elicitation.ExpiresAt - clock.GetUtcNow() + TimeSpan.FromSeconds(1)));
        });

        // The server allowed ten minutes for each; the second request had the three that were left.
        Assert.Equal([Start.AddMinutes(10), Start.AddMinutes(7).AddMinutes(3)], expiries);
        var closed = turn.Events.OfType<NhAssistantElicitationClosedEvent>().Select(evt => evt.Elicitation.Status);
        Assert.Equal([NhAssistantElicitationStatuses.Accepted, NhAssistantElicitationStatuses.Expired], closed);
        Assert.Equal(["confirm:A-401:" + Reason, "confirm:A-402:cancel"], desk.Calls);
        Assert.Contains(host.Business.Events, evt => evt.Kind == NhAssistantAuditEventKind.McpElicitationClosed && evt.ResultCode == "form-expired");
        Assert.Equal(NhAssistantTurnStatuses.Completed, turn.Single<NhAssistantTurnCompletedEvent>().Status);
    }

    [Theory]
    [InlineData(AssistantTestProvider.PostgreSql)]
    [InlineData(AssistantTestProvider.SqlServer)]
    public async Task A_turn_that_waits_for_its_person_longer_than_its_timeout_is_not_taken_over_as_abandoned(AssistantTestProvider provider)
    {
        var clock = new AssistantManualClock(Start);
        var model = new NhAiScriptedChatClient()
            .RespondWithFunctionCall(ConfirmFunction, new { order = "A-403" })
            .RespondWithText("Order A-403 is confirmed.");
        var desk = new OrderDesk();
        await using var host = await CreateHostAsync(model, desk, clock, TimeSpan.FromSeconds(60), turnClock: true, provider: provider);
        await ConnectAsync(host, waitMinutes: null);
        var conversation = await host.CreateConversationAsync();
        string? anotherRequest = "not sent";

        var turn = await host.SendWhileElicitationsWaitAsync(conversation.Id, "Confirm order A-403.", async elicitation =>
        {
            // The person has been asked for four minutes: the conversation's last update is older than the
            // turn's minute and the minute a running conversation is given beyond it.
            var longAgo = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(4);
            await SetLastUpdateAsync(host, conversation.Id, longAgo);

            // The heartbeat renews the turn's claim within its interval on the turn's clock.
            await Task.Run(() => clock.Advance(TimeSpan.FromSeconds(30)));
            await WaitUntilAsync(async () => (await host.ReloadAsync(conversation.Id)).UpdatedAt > longAgo);

            // Another request of the person finds the chat busy instead of taking it over.
            anotherRequest = (await host.SendAsync(conversation.Id, "Are you still there?")).StartCode;
            await AnswerAsync(host, conversation.Id, elicitation, NhAssistantElicitationActions.Accept, Reason);
        });

        Assert.Equal(NhAssistantErrorCodes.ConversationBusy, anotherRequest);
        Assert.Equal(["confirm:A-403:" + Reason], desk.Calls);
        Assert.Equal(NhAssistantTurnStatuses.Completed, turn.Single<NhAssistantTurnCompletedEvent>().Status);
        Assert.Equal(NhAssistantConversationStatuses.Idle, (await host.ReloadAsync(conversation.Id)).Status);
    }

    [Fact]
    public async Task Another_persons_call_of_the_same_tool_runs_while_the_first_persons_form_is_open()
    {
        var clock = new AssistantManualClock(Start);
        var model = new ModelPerPerson(
            new NhAiScriptedChatClient()
                .RespondWithFunctionCall(ConfirmFunction, new { order = "A-404" })
                .RespondWithText("Order A-404 is confirmed."),
            new NhAiScriptedChatClient()
                .RespondWithFunctionCall(ConfirmFunction, new { order = "B-404" })
                .RespondWithText("Order B-404 is confirmed."),
            "B-404");
        var desk = new OrderDesk();
        await using var host = await CreateHostAsync(model, desk, clock, TimeSpan.FromMinutes(15));
        await ConnectAsync(host, waitMinutes: null);
        var first = await host.CreateConversationAsync();
        var second = await host.CreateConversationAsync(Colleague);
        TurnResult? other = null;

        var turn = await host.SendWhileElicitationsWaitAsync(first.Id, "Confirm order A-404.", async elicitation =>
        {
            // The tool runs one call at a time; the first person's open form does not hold it. The bound only
            // matters when the colleague's call would wait for the slot.
            other = await host.SendAsync(second.Id, "Confirm order B-404.", Colleague).WaitAsync(TimeSpan.FromSeconds(30));
            await AnswerAsync(host, first.Id, elicitation, NhAssistantElicitationActions.Accept, Reason);
        });

        Assert.NotNull(other);
        Assert.Equal(NhAssistantToolCallStatuses.Succeeded, other.Single<NhAssistantToolCompletedEvent>().Status);
        Assert.Equal(NhAssistantToolCallStatuses.Succeeded, turn.Single<NhAssistantToolCompletedEvent>().Status);
        Assert.Equal(["confirm:B-404", "confirm:A-404:" + Reason], desk.Calls);
        Assert.Equal(NhAssistantTurnStatuses.Completed, turn.Single<NhAssistantTurnCompletedEvent>().Status);
    }

    [Fact]
    public async Task A_call_that_finds_the_slot_taken_after_its_person_answered_ends_as_concurrency_limited()
    {
        var clock = new AssistantManualClock(Start);
        var model = new ModelPerPerson(
            new NhAiScriptedChatClient()
                .RespondWithFunctionCall(ConfirmFunction, new { order = "A-405" })
                .RespondWithText("Order A-405 could not be confirmed now."),
            new NhAiScriptedChatClient()
                .RespondWithFunctionCall(ConfirmFunction, new { order = "C-405" })
                .RespondWithText("Order C-405 is confirmed."),
            "C-405");
        var desk = new OrderDesk();
        await using var host = await CreateHostAsync(model, desk, clock, TimeSpan.FromMinutes(15));
        await ConnectAsync(host, waitMinutes: null);
        var first = await host.CreateConversationAsync();
        var second = await host.CreateConversationAsync(Colleague);
        Task<TurnResult>? other = null;

        var turn = await host.SendWhileElicitationsWaitAsync(first.Id, "Confirm order A-405.", async elicitation =>
        {
            // A colleague's call takes the tool's slot while the first person's form is open, and keeps it.
            other = host.SendAsync(second.Id, "Confirm order C-405.", Colleague);
            await desk.Holding.Task.WaitAsync(TimeSpan.FromSeconds(30));

            // The person answers; their call waits for the slot within the tool's own time that was left,
            // which the application's clock passes without waiting.
            await AnswerAsync(host, first.Id, elicitation, NhAssistantElicitationActions.Accept, Reason);
            await WaitUntilAsync(() => Task.FromResult(clock.HasTimerDueWithin(TimeSpan.FromSeconds(31))));
            await Task.Run(() => clock.Advance(TimeSpan.FromSeconds(31)));
        });
        desk.Release.SetResult();
        var colleague = await other!.WaitAsync(TimeSpan.FromSeconds(30));

        // The call ended as a call that finds no slot ends; the server never received the answer.
        var refused = turn.Single<NhAssistantToolCompletedEvent>();
        Assert.Equal(NhAiToolFailureCodes.ConcurrencyLimited, refused.ResultCode);
        Assert.NotEqual(NhAssistantToolCallStatuses.Succeeded, refused.Status);
        Assert.Equal(NhAssistantElicitationStatuses.Accepted, turn.Single<NhAssistantElicitationClosedEvent>().Elicitation.Status);
        Assert.DoesNotContain(desk.Calls, call => call.Contains(Reason, StringComparison.Ordinal));
        Assert.Contains(host.Audit.Records, record => record.ResultCode == NhAiToolFailureCodes.ConcurrencyLimited && record.Outcome == NhAiOutcomeKind.Conflict);
        Assert.Equal(NhAssistantTurnStatuses.Completed, turn.Single<NhAssistantTurnCompletedEvent>().Status);
        Assert.Equal(NhAssistantToolCallStatuses.Succeeded, colleague.Single<NhAssistantToolCompletedEvent>().Status);
        Assert.Contains("confirm:C-405", desk.Calls);
    }

    [Fact]
    public async Task The_persons_stop_ends_the_wait_at_once()
    {
        var clock = new AssistantManualClock(Start);
        var model = new NhAiScriptedChatClient()
            .RespondWithFunctionCall(ConfirmFunction, new { order = "A-406" })
            .RespondWithText("Not reached.");
        var desk = new OrderDesk();
        await using var host = await CreateHostAsync(model, desk, clock, TimeSpan.FromSeconds(60), turnClock: true);
        await ConnectAsync(host, waitMinutes: null);
        var conversation = await host.CreateConversationAsync();

        // The application's clock never moves: only the stop can end the wait. The bound only matters when
        // it does not.
        var turn = await host.SendWhileElicitationsWaitAsync(conversation.Id, "Confirm order A-406.", async _ =>
        {
            await using var scope = host.Services.CreateAsyncScope();
            var stopped = await scope.ServiceProvider.GetRequiredService<INhAssistantTurnRunner>()
                .CancelAsync(await host.FindAccessAsync(conversation.Id), CancellationToken.None);
            Assert.True(stopped.Success);
        }).WaitAsync(TimeSpan.FromSeconds(30));

        var closed = turn.Single<NhAssistantElicitationClosedEvent>().Elicitation;
        Assert.Equal((NhAssistantElicitationStatuses.Cancelled, Start), (closed.Status, closed.ClosedAt));
        Assert.Contains(host.Business.Events, evt => evt.Kind == NhAssistantAuditEventKind.McpElicitationClosed && evt.ResultCode == "form-cancelled");
        Assert.DoesNotContain(desk.Calls, call => call.Contains(Reason, StringComparison.Ordinal));
        Assert.Equal(NhAssistantTurnStatuses.Cancelled, turn.Single<NhAssistantTurnCompletedEvent>().Status);
    }

    [Fact]
    public void A_runs_timeouts_stand_still_while_it_is_paused_and_only_its_own_deadline_moves_later()
    {
        var clock = new AssistantManualClock(Start);
        var deadline = Start.AddSeconds(60);
        var pause = new NhAiRunPause(deadline, clock);
        using var caller = new CancellationTokenSource();
        using var timeout = pause.StartTimeout(TimeSpan.FromSeconds(60), caller.Token);
        var turn = new NhAiInvocationContext("agent-1", "assistant", new Dictionary<string, string>()) { Deadline = deadline };
        var application = turn with { Deadline = Start.AddSeconds(45) };

        clock.Advance(TimeSpan.FromSeconds(20));
        var asked = pause.Pause();
        var askedAgain = pause.Pause();
        clock.Advance(TimeSpan.FromMinutes(4));

        // Overlapping pauses: the timeout stands still and the run's own deadline moves with the pause.
        Assert.True(pause.IsPaused);
        Assert.False(timeout.Token.IsCancellationRequested);
        Assert.Equal(TimeSpan.FromSeconds(40), timeout.Remaining);
        Assert.Equal(TimeSpan.FromMinutes(4), pause.Paused);
        asked.Dispose();
        clock.Advance(TimeSpan.FromMinutes(1));
        askedAgain.Dispose();
        Assert.False(pause.IsPaused);
        Assert.Equal(TimeSpan.FromMinutes(5), pause.Paused);

        using (NhAiRunPause.Enter(pause))
        {
            // Calls of the run read its deadline as it stands now; a deadline the application set stays.
            Assert.Equal(deadline.AddMinutes(5), NhAiRunPause.DeadlineOf(turn));
            Assert.Equal(Start.AddSeconds(45), NhAiRunPause.DeadlineOf(application));
        }
        Assert.Equal(deadline, NhAiRunPause.DeadlineOf(turn));

        // The timeout runs on with what was left, and a timeout started during a pause waits for its end.
        clock.Advance(TimeSpan.FromSeconds(39));
        Assert.False(timeout.Token.IsCancellationRequested);
        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.True(timeout.Token.IsCancellationRequested);
        using var later = pause.Pause();
        using var startedWhilePaused = pause.StartTimeout(TimeSpan.FromSeconds(10), caller.Token);
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.False(startedWhilePaused.Token.IsCancellationRequested);
        Assert.Equal(TimeSpan.FromSeconds(10), startedWhilePaused.Remaining);
    }

    [Fact]
    public void The_callers_own_cancellation_still_ends_a_paused_run()
    {
        var clock = new AssistantManualClock(Start);
        var pause = new NhAiRunPause(Start.AddSeconds(60), clock);
        using var caller = new CancellationTokenSource();
        using var timeout = pause.StartTimeout(TimeSpan.FromSeconds(60), caller.Token);

        using (pause.Pause())
        {
            caller.Cancel();

            Assert.True(timeout.Token.IsCancellationRequested);
        }
    }

    private Task<AssistantTestHost> CreateHostAsync(
        IChatClient model,
        OrderDesk desk,
        AssistantManualClock clock,
        TimeSpan turnTimeout,
        bool turnClock = false,
        AssistantTestProvider provider = AssistantTestProvider.PostgreSql)
    {
        var servers = new InMemoryMcpServers();
        servers.Register(ServerUrl, () => [desk.ConfirmOrder()]);
        return AssistantTestHost.CreateAsync(
            database,
            provider,
            model,
            limits: limits => limits.TurnTimeout = turnTimeout,
            configure: services =>
            {
                services.Replace(ServiceDescriptor.Singleton<INhAssistantMcpClientFactory>(servers));
                services.AddSingleton<TimeProvider>(clock);
                if (turnClock)
                {
                    // The turn's own timeout and its heartbeat follow the manual clock too.
                    services.AddSingleton(new NhAssistantTiming { TurnClock = clock });
                }
            },
            assistantBuilder: assistant => assistant.ConfigureMcp(mcp => mcp.RequireHttps = true));
    }

    /// <summary>
    /// Connects the server with its wait time, enables its tool as a read and assigns it to the agent,
    /// as an administrator does.
    /// </summary>
    private static async Task ConnectAsync(AssistantTestHost host, int? waitMinutes)
    {
        await using (var scope = host.Services.CreateAsyncScope())
        {
            AssistantTestHost.EnterUser(scope.ServiceProvider, "admin-1");
            var administration = scope.ServiceProvider.GetRequiredService<NhAssistantMcpAdministration>();
            var created = await administration.CreateAsync(
                new NhAssistantMcpServerInput(ServerId, "Order desk", ServerUrl, NhAssistantMcpAuthModes.None, null, null, null, true)
                {
                    ElicitationWaitMinutes = waitMinutes
                },
                "admin-1",
                CancellationToken.None);
            Assert.True(created.Success);
            Assert.True((await administration.SyncAsync(ServerId, "admin-1", CancellationToken.None)).Success);
            Assert.True((await administration.UpdateToolAsync(ServerId, "confirm_order", true, NhAssistantMcpToolEffects.ReadOnly, null, "admin-1", CancellationToken.None)).Success);
        }

        await using var assigning = host.Services.CreateAsyncScope();
        var agents = assigning.ServiceProvider.GetRequiredService<NhAssistantAgentAdministration>();
        var catalog = assigning.ServiceProvider.GetRequiredService<NhAssistantAgentCatalog>();
        var agent = (await catalog.FindAsync("project-assistant", includeDisabled: true, CancellationToken.None))!;
        var assigned = await agents.UpdateAsync(
            new NhAssistantAgentInput(
                agent.Id,
                agent.Definition.DisplayNameKey,
                agent.Definition.DescriptionKey,
                agent.Definition.Instructions.Content,
                agent.Definition.ToolSelectors,
                [ServerId],
                agent.Definition.RequiredPolicy,
                agent.Definition.Autonomy),
            agent.Definition.Version,
            "admin-1",
            CancellationToken.None);
        Assert.True(assigned.Success);
    }

    private static async Task AnswerAsync(
        AssistantTestHost host,
        Guid conversationId,
        NhAssistantElicitationView elicitation,
        string action,
        string reason)
    {
        await using var answering = host.Services.CreateAsyncScope();
        var answered = await answering.ServiceProvider.GetRequiredService<NhAssistantElicitationAnswerService>().AnswerAsync(
            conversationId,
            elicitation.ElicitationId,
            AssistantTestHost.UserId,
            null,
            action,
            new Dictionary<string, JsonElement>(StringComparer.Ordinal) { ["reason"] = JsonSerializer.SerializeToElement(reason) },
            CancellationToken.None);
        Assert.True(answered.Success);
    }

    private static async Task SetLastUpdateAsync(AssistantTestHost host, Guid conversationId, DateTimeOffset updatedAt)
    {
        await using var context = host.Services.GetRequiredService<NhAssistantDbContextFactory>().CreateDbContext();
        await context.Conversations
            .Where(conversation => conversation.Id == conversationId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(conversation => conversation.UpdatedAt, updatedAt));
    }

    /// <summary>
    /// Waits until <paramref name="condition"/> holds; the bound only matters when it never does.
    /// </summary>
    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (!await condition())
        {
            await Task.Delay(TimeSpan.FromMilliseconds(20), bound.Token);
        }
    }

    /// <summary>
    /// The model of two people's chats that run at once: each plays its own script, chosen by the order
    /// the person's message names.
    /// </summary>
    private sealed class ModelPerPerson(IChatClient first, IChatClient colleague, string colleaguesOrder) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var read = messages.ToList();
            return ScriptOf(read).GetResponseAsync(read, options, cancellationToken);
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var read = messages.ToList();
            return ScriptOf(read).GetStreamingResponseAsync(read, options, cancellationToken);
        }

        public object? GetService(Type serviceType, object? serviceKey = null)
        {
            return serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;
        }

        public void Dispose()
        {
        }

        private IChatClient ScriptOf(IReadOnlyList<ChatMessage> messages)
        {
            return messages.Any(message => message.Role == ChatRole.User && message.Text.Contains(colleaguesOrder, StringComparison.Ordinal))
                ? colleague
                : first;
        }
    }

    /// <summary>
    /// A server whose one tool confirms an order: an order starting with <c>A</c> asks the person for a
    /// reason first, one starting with <c>C</c> holds the tool until the test releases it, and any other
    /// confirms at once.
    /// </summary>
    private sealed class OrderDesk
    {
        public System.Collections.Concurrent.ConcurrentQueue<string> Calls { get; } = new();

        /// <summary>Set when a <c>C</c> order holds the tool.</summary>
        public TaskCompletionSource Holding { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Lets a <c>C</c> order finish.</summary>
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public McpServerTool ConfirmOrder()
        {
            return McpServerTool.Create(
                async ([Description("Order number")] string order, McpServer server, CancellationToken cancellationToken) =>
                {
                    if (order.StartsWith('C'))
                    {
                        Holding.TrySetResult();
                        await Release.Task.WaitAsync(cancellationToken);
                        Calls.Enqueue("confirm:" + order);
                        return "Confirmed.";
                    }

                    if (!order.StartsWith('A'))
                    {
                        Calls.Enqueue("confirm:" + order);
                        return "Confirmed.";
                    }

                    var answer = await server.ElicitAsync(
                        new ElicitRequestParams
                        {
                            Message = $"The desk needs a reason before it confirms order {order}.",
                            RequestedSchema = new ElicitRequestParams.RequestSchema
                            {
                                Properties = new Dictionary<string, ElicitRequestParams.PrimitiveSchemaDefinition>
                                {
                                    ["reason"] = new ElicitRequestParams.StringSchema { Title = "Reason", MaxLength = 200 }
                                },
                                Required = ["reason"]
                            }
                        },
                        cancellationToken);
                    Calls.Enqueue(answer.IsAccepted
                        ? "confirm:" + order + ":" + answer.Content!["reason"].GetString()
                        : "confirm:" + order + ":" + answer.Action);
                    return answer.IsAccepted ? "Confirmed." : "Not confirmed.";
                },
                new McpServerToolCreateOptions { Name = "confirm_order", Description = "Confirm an order.", ReadOnly = true });
        }
    }
}
