using System.ComponentModel;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using NewHeap.Platform.AI.Chat.AspNet.Mcp;
using NewHeap.Platform.AI.Chat.Runtime;
using NewHeap.Platform.AI.Chat.Tests.Infrastructure;
using NewHeap.Platform.AI.Mcp;
using NewHeap.Platform.AI.Test;
using Xunit;

namespace NewHeap.Platform.AI.Chat.Tests;

/// <summary>
/// An administrator-connected MCP server that asks the person of a chat turn for input while its tool
/// runs (MCP elicitation): as a multi round-trip <c>input_required</c> result, as a request on the
/// call's stream of an older protocol, as a link to a page of its own and as a refusal until a page was
/// finished. The person sees the server's request attributed to the server and answers in the chat; the
/// answer goes straight back to the server and is never stored or audited, and anything the chat cannot
/// show safely is declined without asking anyone.
/// </summary>
public sealed class AssistantMcpElicitationTests(AssistantDatabaseFixture database) : IClassFixture<AssistantDatabaseFixture>
{
    private const string ServerUrl = "http://localhost/orders/mcp";
    private const string ConfirmFunction = "mcp_orders_confirm_order";

    [Fact]
    public async Task A_form_reaches_the_person_and_only_their_fitting_answer_goes_back_to_the_server()
    {
        var model = new NhAiScriptedChatClient()
            .RespondWithFunctionCall(ConfirmFunction, new { order = "A-100" })
            .RespondWithText("Order A-100 is confirmed.");
        var servers = new InMemoryMcpServers();
        servers.Register(ServerUrl, () => [ConfirmWithForm(servers)]);
        await using var host = await CreateHostAsync(model, servers);
        await ConnectAsync(host, "confirm_order");
        var conversation = await host.CreateConversationAsync();

        var (turn, codes) = await host.SendAnsweringElicitationsAsync(
            conversation.Id,
            "Confirm order A-100.",
            elicitation => elicitation.Status == "retry"
                ? (NhAssistantElicitationActions.Accept, Content(("reason", "Customer asked"), ("priority", 2), ("notify", true), ("channel", "phone")))
                // Out of the server's bounds first: the person is told and answers again.
                : (NhAssistantElicitationActions.Accept, Content(("reason", "Customer asked"), ("priority", 9))));

        // The person saw the server's own message, attributed to the server, with its fields.
        var requested = turn.Single<NhAssistantElicitationRequestedEvent>().Elicitation;
        Assert.Equal(("orders", "Orders", NhAssistantElicitationModes.Form), (requested.ServerId, requested.ServerName, requested.Mode));
        Assert.Equal("Planning needs a reason before it confirms order A-100.", requested.Message);
        Assert.Equal(AssistantTestHost.UserId, requested.AddresseeActorId);
        Assert.Equal(
            ["reason:string:True", "priority:integer:True", "notify:boolean:False", "channel:enum:False"],
            requested.Fields.Select(field => $"{field.Name}:{field.Type}:{field.Required}"));
        Assert.Equal(["mail", "phone"], requested.Fields.Single(field => field.Name == "channel").Options!.Select(option => option.Value));
        Assert.Equal([NhAssistantErrorCodes.ElicitationAnswerInvalid, null], codes);

        // The server received the second answer, and the call continued with it.
        Assert.Equal(NhAssistantElicitationStatuses.Accepted, turn.Single<NhAssistantElicitationClosedEvent>().Elicitation.Status);
        Assert.Equal(["confirm:Customer asked:2:True:phone"], servers.Calls);
        Assert.Equal(NhAssistantToolCallStatuses.Succeeded, turn.Single<NhAssistantToolCompletedEvent>().Status);

        // The model never read the server's message; the stored part keeps it without the answer.
        Assert.DoesNotContain(model.Requests, request => request.Messages.Any(message => message.Text.Contains("needs a reason", StringComparison.Ordinal)));
        var stored = Assert.Single((await host.ReadViewAsync(conversation.Id)).Messages.SelectMany(message => message.Parts).OfType<NhAssistantElicitationView>());
        Assert.Equal(NhAssistantElicitationStatuses.Accepted, stored.Status);
        Assert.NotNull(stored.ClosedAt);
        var partsJson = await StoredPartsJsonAsync(host, conversation.Id);
        Assert.DoesNotContain("Customer asked", partsJson, StringComparison.Ordinal);

        // The audit names the server, the call, the mode and the outcome, never the answer.
        var asked = Assert.Single(host.Business.Events, evt => evt.Kind == NhAssistantAuditEventKind.McpElicitationRequested);
        var closed = Assert.Single(host.Business.Events, evt => evt.Kind == NhAssistantAuditEventKind.McpElicitationClosed);
        Assert.Equal(("orders", "mcp.orders.confirm-order", "form"), (asked.ObjectId, asked.ToolId, asked.ResultCode));
        Assert.Equal(("orders", requested.InvocationId, "form-accepted"), (closed.ObjectId, closed.InvocationId!.Value, closed.ResultCode));
    }

    [Fact]
    public async Task Only_the_person_of_the_turn_answers_and_a_decline_goes_back_through_an_older_protocol()
    {
        var model = new NhAiScriptedChatClient()
            .RespondWithFunctionCall(ConfirmFunction, new { order = "A-101" })
            .RespondWithText("Order A-101 was not confirmed.");
        // A server of the 2025-11-25 revision asks with elicitation/create on the call's stream.
        var servers = new InMemoryMcpServers { ProtocolVersion = "2025-11-25" };
        servers.Register(ServerUrl, () => [ConfirmWithForm(servers)]);
        await using var host = await CreateHostAsync(model, servers);
        await ConnectAsync(host, "confirm_order");
        var conversation = await host.CreateConversationAsync();

        var (turn, codes) = await host.SendAnsweringElicitationsAsync(
            conversation.Id,
            "Confirm order A-101.",
            _ => (NhAssistantElicitationActions.Decline, null),
            answeredBy: "user-2");

        // Someone outside the conversation cannot even find it; the person of the turn answered next.
        Assert.Equal([NhAssistantErrorCodes.ConversationNotFound, null], codes);
        Assert.Equal(NhAssistantElicitationStatuses.Declined, turn.Single<NhAssistantElicitationClosedEvent>().Elicitation.Status);
        Assert.Equal(["confirm:decline"], servers.Calls);
        Assert.Contains(host.Business.Events, evt => evt.Kind == NhAssistantAuditEventKind.McpElicitationClosed && evt.ResultCode == "form-declined");
    }

    [Fact]
    public async Task A_link_is_shown_for_the_person_to_open_and_their_done_lets_the_stateless_server_continue()
    {
        var model = new NhAiScriptedChatClient()
            .RespondWithFunctionCall(ConfirmFunction, new { order = "A-102" })
            .RespondWithText("The planning account is linked.");
        var servers = new InMemoryMcpServers();
        servers.Register(ServerUrl, () => [ConfirmWithLink(servers, "https://accounts.example.com/link?state=4711")]);
        await using var host = await CreateHostAsync(model, servers);
        await ConnectAsync(host, "confirm_order");
        var conversation = await host.CreateConversationAsync();

        var (turn, codes) = await host.SendAnsweringElicitationsAsync(
            conversation.Id,
            "Confirm order A-102.",
            _ => (NhAssistantElicitationActions.Accept, null));

        var requested = turn.Single<NhAssistantElicitationRequestedEvent>().Elicitation;
        Assert.Equal(NhAssistantElicitationModes.Url, requested.Mode);
        Assert.Equal("https://accounts.example.com/link?state=4711", requested.Url);
        Assert.Equal("accounts.example.com", requested.UrlHost);
        Assert.Empty(requested.Fields);
        Assert.Equal([null], codes);
        // The server's request state came back unchanged with the person's answer.
        Assert.Equal(["link:accept:link-state"], servers.Calls);
        Assert.Contains(host.Business.Events, evt => evt.Kind == NhAssistantAuditEventKind.McpElicitationClosed && evt.ResultCode == "url-accepted");
    }

    [Fact]
    public async Task A_call_refused_until_a_page_was_finished_runs_again_once_the_person_finished_it()
    {
        var model = new NhAiScriptedChatClient()
            .RespondWithFunctionCall(ConfirmFunction, new { order = "A-103" })
            .RespondWithText("Order A-103 is confirmed.");
        var servers = new InMemoryMcpServers();
        var connected = false;
        servers.Register(ServerUrl, () => [ConfirmAfterConnecting(servers, () => connected)]);
        await using var host = await CreateHostAsync(model, servers);
        await ConnectAsync(host, "confirm_order");
        var conversation = await host.CreateConversationAsync();

        var (turn, _) = await host.SendAnsweringElicitationsAsync(
            conversation.Id,
            "Confirm order A-103.",
            elicitation =>
            {
                Assert.Equal("https://accounts.example.com/connect", elicitation.Url);
                connected = true;
                return (NhAssistantElicitationActions.Accept, null);
            });

        Assert.Equal(["refused", "confirmed"], servers.Calls);
        Assert.Equal(NhAssistantToolCallStatuses.Succeeded, turn.Single<NhAssistantToolCompletedEvent>().Status);
    }

    [Fact]
    public async Task A_cancelled_page_fails_the_call_without_running_it_again()
    {
        var model = new NhAiScriptedChatClient()
            .RespondWithFunctionCall(ConfirmFunction, new { order = "A-104" })
            .RespondWithText("Order A-104 could not be confirmed.");
        var servers = new InMemoryMcpServers();
        servers.Register(ServerUrl, () => [ConfirmAfterConnecting(servers, () => false)]);
        await using var host = await CreateHostAsync(model, servers);
        await ConnectAsync(host, "confirm_order");
        var conversation = await host.CreateConversationAsync();

        var (turn, _) = await host.SendAnsweringElicitationsAsync(
            conversation.Id,
            "Confirm order A-104.",
            _ => (NhAssistantElicitationActions.Cancel, null));

        Assert.Equal(["refused"], servers.Calls);
        var completed = turn.Single<NhAssistantToolCompletedEvent>();
        Assert.Equal((NhAssistantToolCallStatuses.Failed, NhAiMcpImportedToolCodes.UrlElicitationRequired), (completed.Status, completed.ResultCode));
        Assert.Equal(NhAssistantElicitationStatuses.Cancelled, turn.Single<NhAssistantElicitationClosedEvent>().Elicitation.Status);
    }

    [Theory]
    [InlineData("multi-select")]
    [InlineData("plain-http-link")]
    public async Task What_the_chat_cannot_show_safely_is_declined_without_asking_anyone(string kind)
    {
        var model = new NhAiScriptedChatClient()
            .RespondWithFunctionCall(ConfirmFunction, new { order = "A-105" })
            .RespondWithText("Order A-105 was not confirmed.");
        var servers = new InMemoryMcpServers();
        servers.Register(ServerUrl, () => [kind == "multi-select" ? ConfirmWithMultiSelect(servers) : ConfirmWithLink(servers, "http://accounts.example.com/link")]);
        await using var host = await CreateHostAsync(model, servers);
        await ConnectAsync(host, "confirm_order");
        var conversation = await host.CreateConversationAsync();

        var (turn, codes) = await host.SendAnsweringElicitationsAsync(
            conversation.Id,
            "Confirm order A-105.",
            _ => throw new InvalidOperationException("Nobody may be asked."));

        Assert.Empty(codes);
        Assert.Empty(turn.Events.OfType<NhAssistantElicitationRequestedEvent>());
        Assert.Single(servers.Calls, call => call.EndsWith(":decline", StringComparison.Ordinal) || call.StartsWith("link:decline", StringComparison.Ordinal));
        var mode = kind == "multi-select" ? "form" : "url";
        Assert.Contains(host.Business.Events, evt => evt.Kind == NhAssistantAuditEventKind.McpElicitationClosed && evt.ResultCode == mode + "-unsupported");
    }

    [Fact]
    public async Task A_request_outside_a_governed_call_of_its_server_is_declined()
    {
        var handler = NhAssistantMcpElicitation.CreateHandler("orders", "Orders");

        var result = await handler(new ElicitRequestParams { Message = "Who are you?" }, CancellationToken.None);

        Assert.Equal(NhAssistantElicitationActions.Decline, result.Action);
        Assert.Null(result.Content);
    }

    [Fact]
    public void Only_the_addressee_answers_a_waiting_request_once()
    {
        var registry = new NhAssistantElicitationRegistry();
        var conversationId = Guid.NewGuid();
        var formId = Guid.NewGuid();
        var linkId = Guid.NewGuid();
        using var form = registry.Register(formId, conversationId, "user-1", NhAssistantElicitationModes.Form, [new("reason", NhAssistantElicitationFieldTypes.String, "Reason", true)]);
        using var link = registry.Register(linkId, conversationId, "user-1", NhAssistantElicitationModes.Url, []);

        Assert.Equal(NhAssistantErrorCodes.ElicitationForbidden, registry.Answer(conversationId, formId, "user-2", NhAssistantElicitationActions.Decline, null));
        Assert.Equal(NhAssistantErrorCodes.ElicitationNotFound, registry.Answer(Guid.NewGuid(), formId, "user-1", NhAssistantElicitationActions.Decline, null));
        Assert.Equal(NhAssistantErrorCodes.ElicitationAnswerInvalid, registry.Answer(conversationId, formId, "user-1", "approve", null));
        Assert.Equal(NhAssistantErrorCodes.ElicitationAnswerInvalid, registry.Answer(conversationId, linkId, "user-1", NhAssistantElicitationActions.Accept, Content(("token", "secret"))));
        Assert.Null(registry.Answer(conversationId, formId, "user-1", NhAssistantElicitationActions.Accept, Content(("reason", "Late"))));
        Assert.Equal(NhAssistantErrorCodes.ElicitationNotPending, registry.Answer(conversationId, formId, "user-1", NhAssistantElicitationActions.Decline, null));
        Assert.Equal(NhAssistantErrorCodes.ElicitationNotPending, registry.Answer(conversationId, Guid.NewGuid(), "user-1", NhAssistantElicitationActions.Decline, null));
        Assert.Equal("Late", form.Answer.Result.Content!["reason"].GetString());
    }

    [Fact]
    public void A_form_answer_fits_only_its_fields()
    {
        NhAssistantElicitationField[] fields =
        [
            new("reason", NhAssistantElicitationFieldTypes.String, "Reason", true) { MaxLength = 5 },
            new("priority", NhAssistantElicitationFieldTypes.Integer, "Priority", false) { Minimum = 1, Maximum = 5 },
            new("channel", NhAssistantElicitationFieldTypes.Enum, "Channel", false) { Options = [new("mail", "Mail")] }
        ];

        Assert.True(NhAssistantElicitationAnswers.Fits(fields, Content(("reason", "Late"))));
        Assert.True(NhAssistantElicitationAnswers.Fits(fields, Content(("reason", "Late"), ("priority", 5), ("channel", "mail"))));
        Assert.False(NhAssistantElicitationAnswers.Fits(fields, Content(("priority", 2))));
        Assert.False(NhAssistantElicitationAnswers.Fits(fields, Content(("reason", "Too late"))));
        Assert.False(NhAssistantElicitationAnswers.Fits(fields, Content(("reason", "Late"), ("priority", 2.5))));
        Assert.False(NhAssistantElicitationAnswers.Fits(fields, Content(("reason", "Late"), ("channel", "phone"))));
        Assert.False(NhAssistantElicitationAnswers.Fits(fields, Content(("reason", "Late"), ("extra", true))));
    }

    /// <summary>
    /// A tool that asks for a reason, a priority, whether to notify and a channel before it confirms.
    /// </summary>
    private static McpServerTool ConfirmWithForm(InMemoryMcpServers servers)
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
                                ["reason"] = new ElicitRequestParams.StringSchema { Title = "Reason", MaxLength = 200 },
                                ["priority"] = new ElicitRequestParams.NumberSchema { Type = "integer", Title = "Priority", Minimum = 1, Maximum = 5 },
                                ["notify"] = new ElicitRequestParams.BooleanSchema { Title = "Notify the customer" },
                                ["channel"] = new ElicitRequestParams.UntitledSingleSelectEnumSchema { Title = "Channel", Enum = ["mail", "phone"] }
                            },
                            Required = ["reason", "priority"]
                        }
                    },
                    cancellationToken);
                if (!answer.IsAccepted)
                {
                    servers.Calls.Enqueue("confirm:" + answer.Action);
                    return "Not confirmed.";
                }

                var content = answer.Content!;
                servers.Calls.Enqueue(string.Join(
                    ":",
                    "confirm",
                    content["reason"].GetString(),
                    content["priority"].GetInt32(),
                    content["notify"].GetBoolean(),
                    content["channel"].GetString()));
                return "Confirmed.";
            },
            new McpServerToolCreateOptions { Name = "confirm_order", Description = "Confirm an order.", ReadOnly = true });
    }

    /// <summary>
    /// A tool that asks for several channels at once, which the chat does not ask.
    /// </summary>
    private static McpServerTool ConfirmWithMultiSelect(InMemoryMcpServers servers)
    {
        return McpServerTool.Create(
            async ([Description("Order number")] string order, McpServer server, CancellationToken cancellationToken) =>
            {
                var answer = await server.ElicitAsync(
                    new ElicitRequestParams
                    {
                        Message = $"Which channels may planning use for order {order}?",
                        RequestedSchema = new ElicitRequestParams.RequestSchema
                        {
                            Properties = new Dictionary<string, ElicitRequestParams.PrimitiveSchemaDefinition>
                            {
                                ["channels"] = new ElicitRequestParams.UntitledMultiSelectEnumSchema
                                {
                                    Items = new ElicitRequestParams.UntitledEnumItemsSchema { Enum = ["mail", "phone"] }
                                }
                            }
                        }
                    },
                    cancellationToken);
                servers.Calls.Enqueue("confirm:" + answer.Action);
                return "Not confirmed.";
            },
            new McpServerToolCreateOptions { Name = "confirm_order", Description = "Confirm an order.", ReadOnly = true });
    }

    /// <summary>
    /// A stateless tool that sends the person to a page of its own with a multi round-trip result, and
    /// reads its request state and the person's answer on the retry.
    /// </summary>
    private static McpServerTool ConfirmWithLink(InMemoryMcpServers servers, string url)
    {
        return McpServerTool.Create(
            ([Description("Order number")] string order, RequestContext<CallToolRequestParams> request) =>
            {
                if (request.Params?.InputResponses is { } responses && responses.TryGetValue("link", out var response))
                {
                    var answer = response.Deserialize(InputResponse.ElicitResultJsonTypeInfo)!;
                    servers.Calls.Enqueue($"link:{answer.Action}:{request.Params.RequestState}");
                    return answer.IsAccepted ? "Linked." : "Not linked.";
                }

                throw new InputRequiredException(
                    new Dictionary<string, InputRequest>
                    {
                        ["link"] = InputRequest.ForElicitation(new ElicitRequestParams
                        {
                            Mode = "url",
                            ElicitationId = "link-" + order,
                            Url = url,
                            Message = "Link your planning account to confirm the order."
                        })
                    },
                    "link-state");
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

    private static IReadOnlyDictionary<string, JsonElement> Content(params (string Name, object Value)[] values)
    {
        return values.ToDictionary(value => value.Name, value => JsonSerializer.SerializeToElement(value.Value), StringComparer.Ordinal);
    }

    private static async Task<string> StoredPartsJsonAsync(AssistantTestHost host, Guid conversationId)
    {
        var store = host.Services.GetRequiredService<Persistence.INhAssistantStore>();
        var messages = await store.GetMessagesAsync(conversationId, 50, CancellationToken.None);
        return string.Join("\n", messages.Select(message => message.PartsJson));
    }

    private Task<AssistantTestHost> CreateHostAsync(NhAiScriptedChatClient model, InMemoryMcpServers servers)
    {
        return AssistantTestHost.CreateAsync(
            database,
            AssistantTestProvider.PostgreSql,
            model,
            configure: services => services.Replace(ServiceDescriptor.Singleton<INhAssistantMcpClientFactory>(servers)),
            assistantBuilder: assistant => assistant.ConfigureMcp(mcp => mcp.RequireHttps = true));
    }

    /// <summary>
    /// Connects the planning server, enables <paramref name="remoteName"/> as a read and assigns the
    /// server to the agent, as an administrator does.
    /// </summary>
    private static async Task ConnectAsync(AssistantTestHost host, string remoteName)
    {
        await using (var scope = host.Services.CreateAsyncScope())
        {
            AssistantTestHost.EnterUser(scope.ServiceProvider, "admin-1");
            var administration = scope.ServiceProvider.GetRequiredService<NhAssistantMcpAdministration>();
            Assert.True((await administration.CreateAsync(
                new NhAssistantMcpServerInput("orders", "Orders", ServerUrl, NhAssistantMcpAuthModes.None, null, null, null, true),
                "admin-1",
                CancellationToken.None)).Success);
            Assert.True((await administration.SyncAsync("orders", "admin-1", CancellationToken.None)).Success);
            Assert.True((await administration.UpdateToolAsync("orders", remoteName, true, NhAssistantMcpToolEffects.ReadOnly, null, "admin-1", CancellationToken.None)).Success);
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
                ["orders"],
                agent.Definition.RequiredPolicy,
                agent.Definition.Autonomy),
            agent.Definition.Version,
            "admin-1",
            CancellationToken.None);
        Assert.True(assigned.Success);
    }
}
