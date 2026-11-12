using System.ComponentModel;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using NewHeap.Platform.AI.Chat.AspNet.Mcp;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.AI.Chat.Runtime;
using NewHeap.Platform.AI.Chat.Tests.Infrastructure;
using NewHeap.Platform.AI.Mcp;
using NewHeap.Platform.AI.Test;
using Xunit;

namespace NewHeap.Platform.AI.Chat.Tests;

/// <summary>
/// An MCP server's request for input waits the server's own wait time for the person of the turn,
/// five minutes unless an administrator or the application's code sets one to ten, instead of the
/// tool's own timeout: the clock of that timeout stands still while the person is asked and runs on
/// once they answered. The wait follows the application's clock; a request nobody answers in time is
/// cancelled, and the audit records that it expired. The turn stands still as well
/// (<see cref="AssistantMcpElicitationTurnPauseTests"/>).
/// </summary>
public sealed class AssistantMcpElicitationWaitTests(AssistantDatabaseFixture database) : IClassFixture<AssistantDatabaseFixture>
{
    private const string ServerId = "slow-orders";
    private const string ServerUrl = "http://localhost/slow-orders/mcp";
    private const string ConfirmFunction = "mcp_slow-orders_confirm_order";
    private static readonly DateTimeOffset Start = new(2026, 11, 11, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task A_person_who_answers_after_the_tools_own_timeout_within_the_servers_wait_time_is_heard()
    {
        var clock = new AssistantManualClock(Start);
        var model = new NhAiScriptedChatClient()
            .RespondWithFunctionCall(ConfirmFunction, new { order = "A-200" })
            .RespondWithText("Order A-200 is confirmed.");
        var servers = new InMemoryMcpServers();
        servers.Register(ServerUrl, () => [ConfirmWithReason(servers)]);
        await using var host = await CreateHostAsync(model, servers, clock, TimeSpan.FromMinutes(15));
        await ConnectAsync(host, waitMinutes: 3);
        var conversation = await host.CreateConversationAsync();
        var pausedWhileAsked = false;
        DateTimeOffset? expiresAt = null;
        string? answerCode = "not answered";

        // Observations only while the call waits, so the turn always ends; the test asserts afterwards.
        var turn = await host.SendWhileElicitationsWaitAsync(conversation.Id, "Confirm order A-200.", async elicitation =>
        {
            expiresAt = elicitation.ExpiresAt;
            pausedWhileAsked = NhAiMcpCallContext.SoleCallOf(ServerId)?.Timeout is { IsPaused: true, Remaining.Ticks: > 0 } timeout
                && timeout.Remaining <= TimeSpan.FromSeconds(30);

            // The person takes two minutes, four times the tool's own timeout.
            clock.Advance(TimeSpan.FromMinutes(2));
            await using var answering = host.Services.CreateAsyncScope();
            var answered = await answering.ServiceProvider.GetRequiredService<NhAssistantElicitationAnswerService>().AnswerAsync(
                conversation.Id,
                elicitation.ElicitationId,
                AssistantTestHost.UserId,
                null,
                NhAssistantElicitationActions.Accept,
                new Dictionary<string, JsonElement>(StringComparer.Ordinal) { ["reason"] = JsonSerializer.SerializeToElement("Customer called back") },
                CancellationToken.None);
            answerCode = answered.Success ? null : answered.GetResultItems().First().Name;
        });

        // The request waited the server's three minutes, not the tool's own 30 seconds, whose clock stood still.
        Assert.Equal(Start.AddMinutes(3), expiresAt);
        Assert.True(pausedWhileAsked);
        Assert.Null(answerCode);
        Assert.Equal(NhAssistantElicitationStatuses.Accepted, turn.Single<NhAssistantElicitationClosedEvent>().Elicitation.Status);
        Assert.Equal(["confirm:Customer called back"], servers.Calls);
        Assert.Equal(NhAssistantToolCallStatuses.Succeeded, turn.Single<NhAssistantToolCompletedEvent>().Status);
        Assert.Contains(host.Business.Events, evt => evt.Kind == NhAssistantAuditEventKind.McpElicitationClosed && evt.ResultCode == "form-accepted");
    }

    [Fact]
    public async Task A_page_a_refused_call_needs_may_take_the_person_longer_than_the_tools_own_timeout()
    {
        var clock = new AssistantManualClock(Start);
        var model = new NhAiScriptedChatClient()
            .RespondWithFunctionCall(ConfirmFunction, new { order = "A-203" })
            .RespondWithText("Order A-203 is confirmed.");
        var servers = new InMemoryMcpServers();
        var connected = false;
        servers.Register(ServerUrl, () => [ConfirmAfterConnecting(servers, () => connected)]);
        await using var host = await CreateHostAsync(model, servers, clock, TimeSpan.FromMinutes(15));
        await ConnectAsync(host, waitMinutes: 10);
        var conversation = await host.CreateConversationAsync();
        var pausedWhileAsked = false;
        DateTimeOffset? expiresAt = null;

        // The server refuses the call until the person finished its page (URL_ELICITATION_REQUIRED).
        var turn = await host.SendWhileElicitationsWaitAsync(conversation.Id, "Confirm order A-203.", async elicitation =>
        {
            expiresAt = elicitation.ExpiresAt;
            pausedWhileAsked = NhAiMcpCallContext.SoleCallOf(ServerId)?.Timeout is { IsPaused: true };

            // Finishing the page takes the person nine minutes.
            clock.Advance(TimeSpan.FromMinutes(9));
            connected = true;
            await using var answering = host.Services.CreateAsyncScope();
            await answering.ServiceProvider.GetRequiredService<NhAssistantElicitationAnswerService>().AnswerAsync(
                conversation.Id,
                elicitation.ElicitationId,
                AssistantTestHost.UserId,
                null,
                NhAssistantElicitationActions.Accept,
                null,
                CancellationToken.None);
        });

        Assert.Equal(Start.AddMinutes(10), expiresAt);
        Assert.True(pausedWhileAsked);
        Assert.Equal(["refused", "confirmed"], servers.Calls);
        Assert.Equal(NhAssistantToolCallStatuses.Succeeded, turn.Single<NhAssistantToolCompletedEvent>().Status);
        Assert.Contains(host.Business.Events, evt => evt.Kind == NhAssistantAuditEventKind.McpElicitationClosed && evt.ResultCode == "url-accepted");
    }

    [Fact]
    public async Task A_request_nobody_answers_is_cancelled_when_the_servers_wait_time_ends_on_the_application_clock()
    {
        var clock = new AssistantManualClock(Start);
        var model = new NhAiScriptedChatClient()
            .RespondWithFunctionCall(ConfirmFunction, new { order = "A-201" })
            .RespondWithText("Nobody confirmed order A-201.");
        var servers = new InMemoryMcpServers();
        servers.Register(ServerUrl, () => [ConfirmWithReason(servers)]);
        await using var host = await CreateHostAsync(model, servers, clock, TimeSpan.FromMinutes(15));
        await ConnectAsync(host, waitMinutes: 2);
        var conversation = await host.CreateConversationAsync();

        DateTimeOffset? expiresAt = null;

        var turn = await host.SendWhileElicitationsWaitAsync(conversation.Id, "Confirm order A-201.", async elicitation =>
        {
            expiresAt = elicitation.ExpiresAt;
            // Nobody answers; the application's clock passes the end of the wait without real waiting. The
            // clock moves on a pool thread, where a timer of the application's clock fires in production.
            await Task.Run(() => clock.Advance(elicitation.ExpiresAt - clock.GetUtcNow() + TimeSpan.FromSeconds(1)));
        });

        Assert.Equal(Start.AddMinutes(2), expiresAt);
        var closed = turn.Single<NhAssistantElicitationClosedEvent>().Elicitation;
        Assert.Equal(NhAssistantElicitationStatuses.Expired, closed.Status);
        Assert.Equal(Start.AddMinutes(2).AddSeconds(1), closed.ClosedAt);
        // The server read a cancellation and nothing else; the call itself went on and ended.
        Assert.Equal(["confirm:cancel"], servers.Calls);
        Assert.Equal(NhAssistantToolCallStatuses.Succeeded, turn.Single<NhAssistantToolCompletedEvent>().Status);
        var audited = Assert.Single(host.Business.Events, evt => evt.Kind == NhAssistantAuditEventKind.McpElicitationClosed);
        Assert.Equal((ServerId, "form-expired"), (audited.ObjectId, audited.ResultCode));
    }

    [Fact]
    public void A_tool_calls_own_time_stands_still_while_a_person_is_asked_and_runs_on_with_what_was_left()
    {
        var clock = new AssistantManualClock(Start);
        using var caller = new CancellationTokenSource();
        using var composition = new NhAiToolCallTimeout(TimeSpan.FromSeconds(60), clock, caller.Token);
        using var compositionScope = NhAiToolCallTimeout.Enter(composition);
        using var step = new NhAiToolCallTimeout(TimeSpan.FromSeconds(30), clock, composition.Token);

        clock.Advance(TimeSpan.FromSeconds(10));
        var asked = step.Pause();
        var askedAgain = step.Pause();
        clock.Advance(TimeSpan.FromMinutes(10));

        // A pause stops the step's clock and the clock of the call it runs within.
        Assert.True(step.IsPaused);
        Assert.True(composition.IsPaused);
        Assert.False(step.Token.IsCancellationRequested);
        Assert.Equal(TimeSpan.FromSeconds(20), step.Remaining);
        Assert.Equal(TimeSpan.FromSeconds(50), composition.Remaining);

        // Overlapping pauses: the clock runs again when the last one ends.
        asked.Dispose();
        Assert.True(step.IsPaused);
        askedAgain.Dispose();
        Assert.False(step.IsPaused);
        Assert.False(composition.IsPaused);

        clock.Advance(TimeSpan.FromSeconds(19));
        Assert.False(step.Token.IsCancellationRequested);
        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.True(step.Token.IsCancellationRequested);
        Assert.False(composition.Token.IsCancellationRequested);
        Assert.Equal(TimeSpan.FromSeconds(29), composition.Remaining);
    }

    [Fact]
    public void The_callers_own_cancellation_still_ends_a_call_while_a_person_is_asked()
    {
        var clock = new AssistantManualClock(Start);
        using var turn = new CancellationTokenSource();
        using var call = new NhAiToolCallTimeout(TimeSpan.FromSeconds(30), clock, turn.Token);

        using (call.Pause())
        {
            turn.Cancel();

            Assert.True(call.Token.IsCancellationRequested);
        }
    }

    [Theory]
    [InlineData(AssistantTestProvider.PostgreSql)]
    [InlineData(AssistantTestProvider.SqlServer)]
    public async Task The_wait_time_of_a_server_round_trips_through_the_administration_within_one_to_ten_minutes(AssistantTestProvider provider)
    {
        await using var app = await AssistantWebApplication.StartAsync(database, new NhAiScriptedChatClient(), provider: provider);
        using var admin = app.CreateClient(admin: true);
        var input = new
        {
            id = "planning",
            displayName = "Planning",
            url = "http://localhost/planning/mcp",
            authMode = "none",
            headerName = (string?)null,
            requiredPolicy = (string?)null,
            isEnabled = true,
            elicitationWaitMinutes = (int?)null
        };

        using var created = await admin.PostAsync("/api/assistant/admin/mcp-servers", ServerSentEventReader.Json(input));
        using var tooShort = await admin.PutAsync("/api/assistant/admin/mcp-servers/planning", ServerSentEventReader.Json(input with { elicitationWaitMinutes = 0 }));
        using var tooLong = await admin.PutAsync("/api/assistant/admin/mcp-servers/planning", ServerSentEventReader.Json(input with { elicitationWaitMinutes = 11 }));
        using var longest = await admin.PutAsync("/api/assistant/admin/mcp-servers/planning", ServerSentEventReader.Json(input with { elicitationWaitMinutes = 10 }));
        using var kept = await admin.PutAsync("/api/assistant/admin/mcp-servers/planning", ServerSentEventReader.Json(input with { displayName = "Planning desk" }));
        using var listed = await admin.GetAsync("/api/assistant/admin/mcp-servers");

        // A new server waits five minutes; an administrator sets one to ten, and an omitted value keeps it.
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal(5, (await ReadJsonAsync(created)).GetProperty("elicitationWaitMinutes").GetInt32());
        foreach (var refused in new[] { tooShort, tooLong })
        {
            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
            Assert.Equal("invalid", (await ReadJsonAsync(refused)).GetProperty("errors").GetProperty("elicitationWaitMinutes")[0].GetString());
        }
        Assert.Equal(10, (await ReadJsonAsync(longest)).GetProperty("elicitationWaitMinutes").GetInt32());
        Assert.Equal(10, (await ReadJsonAsync(kept)).GetProperty("elicitationWaitMinutes").GetInt32());
        var server = (await ReadJsonAsync(listed)).EnumerateArray().Single(item => item.GetProperty("id").GetString() == "planning");
        Assert.Equal(10, server.GetProperty("elicitationWaitMinutes").GetInt32());
        await using var scope = app.Services.CreateAsyncScope();
        var stored = await scope.ServiceProvider.GetRequiredService<NhAssistantAdminStore>().FindMcpServerAsync("planning", CancellationToken.None);
        Assert.Equal(10, stored!.ElicitationWaitMinutes);

        // The application's own code sets it on the server's registration, with the same bounds.
        var codeServers = scope.ServiceProvider.GetRequiredService<INhAssistantMcpServers>();
        var registered = await codeServers.CreateAsync(
            new NhAssistantMcpServerRequest("desk", "Desk", "http://localhost/desk/mcp", NhAssistantMcpAuthModes.None, null, null, null, true)
            {
                ElicitationWaitMinutes = 2
            },
            "operations");
        var outOfBounds = await codeServers.UpdateAsync(
            new NhAssistantMcpServerRequest("desk", "Desk", "http://localhost/desk/mcp", NhAssistantMcpAuthModes.None, null, null, null, true)
            {
                ElicitationWaitMinutes = NhAssistantMcpServerLimits.MaxElicitationWaitMinutes + 1
            },
            "operations");

        Assert.True(registered.Success);
        Assert.Equal(2, registered.Data!.ElicitationWaitMinutes);
        Assert.Contains(outOfBounds.GetResultItems(), item => item.Name == "field:elicitationWaitMinutes");
        Assert.Equal(
            (1, 5, 10),
            (NhAssistantMcpServerLimits.MinElicitationWaitMinutes, NhAssistantMcpServerLimits.DefaultElicitationWaitMinutes, NhAssistantMcpServerLimits.MaxElicitationWaitMinutes));
    }

    /// <summary>
    /// A tool that asks for a reason before it confirms, and tells what it heard.
    /// </summary>
    private static McpServerTool ConfirmWithReason(InMemoryMcpServers servers)
    {
        return McpServerTool.Create(
            async ([Description("Order number")] string order, McpServer server, CancellationToken cancellationToken) =>
            {
                var answer = await server.ElicitAsync(
                    new ElicitRequestParams
                    {
                        Message = $"Planning needs a reason before it confirms order {order}.",
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
                if (!answer.IsAccepted)
                {
                    servers.Calls.Enqueue("confirm:" + answer.Action);
                    return "Not confirmed.";
                }

                servers.Calls.Enqueue("confirm:" + answer.Content!["reason"].GetString());
                return "Confirmed.";
            },
            new McpServerToolCreateOptions { Name = "confirm_order", Description = "Confirm an order.", ReadOnly = true });
    }

    /// <summary>
    /// A tool that refuses the call until the person finished the server's connect page.
    /// </summary>
    private static McpServerTool ConfirmAfterConnecting(InMemoryMcpServers servers, Func<bool> connected)
    {
        return McpServerTool.Create(
            ([Description("Order number")] string order) =>
            {
                if (!connected())
                {
                    servers.Calls.Enqueue("refused");
                    throw new UrlElicitationRequiredException(
                        "Connect your planning account first.",
                        [
                            new ElicitRequestParams
                            {
                                Mode = "url",
                                ElicitationId = "connect-" + order,
                                Url = "https://accounts.example.com/connect",
                                Message = "Connect your planning account to confirm orders."
                            }
                        ]);
                }

                servers.Calls.Enqueue("confirmed");
                return "Confirmed.";
            },
            new McpServerToolCreateOptions { Name = "confirm_order", Description = "Confirm an order.", ReadOnly = true });
    }

    private Task<AssistantTestHost> CreateHostAsync(
        NhAiScriptedChatClient model,
        InMemoryMcpServers servers,
        AssistantManualClock clock,
        TimeSpan turnTimeout)
    {
        return AssistantTestHost.CreateAsync(
            database,
            AssistantTestProvider.PostgreSql,
            model,
            limits: limits => limits.TurnTimeout = turnTimeout,
            configure: services =>
            {
                services.Replace(ServiceDescriptor.Singleton<INhAssistantMcpClientFactory>(servers));
                services.AddSingleton<TimeProvider>(clock);
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
                new NhAssistantMcpServerInput(ServerId, "Slow orders", ServerUrl, NhAssistantMcpAuthModes.None, null, null, null, true)
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

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }
}
