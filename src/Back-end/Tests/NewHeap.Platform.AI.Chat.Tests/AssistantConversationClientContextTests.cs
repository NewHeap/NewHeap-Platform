using System.Net;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.AI.Chat.AspNet;
using NewHeap.Platform.AI.Chat.AspNet.AgentRuns;
using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.AI.Chat.Tests.Infrastructure;
using NewHeap.Platform.AI.Test;
using NewHeap.Platform.Common.Models;
using Xunit;
using static NewHeap.Platform.AI.Chat.Tests.Infrastructure.ServerSentEventReader;

namespace NewHeap.Platform.AI.Chat.Tests;

/// <summary>
/// FB52: the page a person has open, from an application's channel
/// (<see cref="NhAssistantConversationMessage.ClientContext"/>). The turn shows it to the model as the
/// person's screen and the message stores it, as <c>POST conversations/{id}/messages</c> does with its
/// <c>clientContext</c>; a context the endpoint would not keep whole is refused with the endpoint's
/// <c>assistant-message-invalid</c>, and a message without one changes nothing. On both relational
/// providers.
/// </summary>
public sealed class AssistantConversationClientContextTests(AssistantDatabaseFixture database) : IClassFixture<AssistantDatabaseFixture>
{
    private const string ChannelCode = "website-widget";

    private static readonly string LongLabel = "Roadmap " + new string('x', 150);

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_channels_page_reaches_the_model_and_is_stored_as_the_endpoint_stores_it(AssistantTestProvider provider)
    {
        var model = new NhAiScriptedChatClient()
            .RespondWithFunctionCall("projects_change_status_v1", new { input = new { projectId = Guid.NewGuid(), status = "Active" } })
            .RespondWithText("Project 7 is active.")
            .RespondWithText("Project 7 is still open.");
        await using var app = await AssistantWebApplication.StartAsync(database, model, provider: provider);
        using var client = app.CreateClient();
        var id = await CreateConversationAsync(client);
        // Markup is neutralized and a long label cut off, as the endpoint does.
        var page = new NhAssistantClientContextDto(
            "/projects/7",
            "<b>Project 7</b>",
            [new NhAssistantClientEntityDto("project", " 7 ", LongLabel)]);

        var sent = await SendAsync(app, id, new NhAssistantConversationMessage("Activate this project.") { ClientContext = page });
        // The turn resumes after the person's approval with the page of its message.
        Assert.True(sent.Success);
        var approval = Assert.Single(sent.Data!.PendingApprovals);
        var decided = await DecideAsync(app, id, approval.ApprovalId, approval.ProposalHash);
        using (var endpoint = await client.PostAsync(
            $"/api/assistant/conversations/{id}/messages",
            Json(new
            {
                text = "And now?",
                clientMessageId = "endpoint-1",
                clientContext = JsonSerializer.SerializeToElement(page, NhAssistantJsonSerializerContext.Default.NhAssistantClientContextDto)
            })))
        {
            Assert.Equal(HttpStatusCode.OK, endpoint.StatusCode);
            await ReadAllAsync(endpoint);
        }

        Assert.Equal(NhAssistantTurnStatuses.WaitingForApproval, sent.Data.Status);
        Assert.Equal(NhAssistantTurnStatuses.Completed, decided.Data!.Status);
        var screen = "<page-data>\n- Route: /projects/7\n- Title: ‹b›Project 7‹/b›\n- Entity: project 7 ("
            + LongLabel[..NhAssistantClientContext.MaxEntityLabelLength] + ")\n</page-data>";
        Assert.Equal(3, model.Requests.Count);
        Assert.All(model.Requests, request => Assert.Contains(screen, request.Options!.Instructions, StringComparison.Ordinal));
        var stored = await UserMessagesAsync(app, id);
        Assert.Equal(2, stored.Count);
        Assert.NotNull(stored[0].ClientContextJson);
        Assert.Equal(stored[1].ClientContextJson, stored[0].ClientContextJson);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_page_the_endpoint_would_not_keep_whole_is_refused_with_its_code(AssistantTestProvider provider)
    {
        var model = new NhAiScriptedChatClient().RespondWithText("Five projects are open.");
        await using var app = await AssistantWebApplication.StartAsync(database, model, provider: provider);
        using var client = app.CreateClient();
        var id = await CreateConversationAsync(client);
        NhAssistantClientContextDto[] invalid =
        [
            // A route without text, which the endpoint ignores.
            new("   ", "Project 7", null),
            // Entities the endpoint drops.
            new("/projects", null, [new NhAssistantClientEntityDto("Project Group", "7", null)]),
            new("/projects", null, [new NhAssistantClientEntityDto("project", " ", null)]),
            new("/projects", null, [new NhAssistantClientEntityDto("project", new string('9', NhAssistantClientContext.MaxEntityIdLength + 1), null)]),
            new("/projects", null, [new NhAssistantClientEntityDto("project", "7\n8", null)]),
            new("/projects", null, [null!]),
            // More entities than the endpoint keeps.
            new("/projects", null, Enumerable.Range(1, NhAssistantClientContext.MaxEntities + 1)
                .Select(index => new NhAssistantClientEntityDto("project", index.ToString(), null))
                .ToArray()),
            // A page the message cannot store, which the endpoint ignores.
            UnstorablePage()
        ];

        foreach (var page in invalid)
        {
            var sent = await SendAsync(app, id, new NhAssistantConversationMessage("What is open?") { ClientContext = page });

            Assert.Equal(NhAssistantErrorCodes.MessageInvalid, CodeOf(sent));
        }

        // Nothing started: the chat waits for nobody, stores no message and the model was not asked.
        Assert.Empty(model.Requests);
        Assert.Empty(await UserMessagesAsync(app, id));
        var conversation = await ReadJsonAsync(await client.GetAsync($"/api/assistant/conversations/{id}"));
        Assert.Equal("idle", conversation.GetProperty("status").GetString());
        // At the limits the page is kept whole.
        var full = new NhAssistantClientContextDto(
            "/projects",
            null,
            Enumerable.Range(1, NhAssistantClientContext.MaxEntities)
                .Select(index => new NhAssistantClientEntityDto("project", new string('9', NhAssistantClientContext.MaxEntityIdLength - 1) + index, null))
                .ToArray());
        var kept = await SendAsync(app, id, new NhAssistantConversationMessage("What is open?") { ClientContext = full });
        Assert.True(kept.Success);
        Assert.Equal(NhAssistantClientContext.MaxEntities, NhAssistantClientContext.FromStorage(Assert.Single(await UserMessagesAsync(app, id)).ClientContextJson)!.Entities.Count);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task Without_a_page_the_turn_shows_no_screen_and_stores_none(AssistantTestProvider provider)
    {
        var model = new NhAiScriptedChatClient().RespondWithText("Nothing is open.");
        await using var app = await AssistantWebApplication.StartAsync(database, model, provider: provider);
        using var client = app.CreateClient();
        var id = await CreateConversationAsync(client);

        var sent = await SendAsync(app, id, new NhAssistantConversationMessage("What is open?"));

        Assert.True(sent.Success);
        Assert.Equal(NhAssistantTurnStatuses.Completed, sent.Data!.Status);
        var instructions = model.Requests[0].Options!.Instructions!;
        Assert.Contains("<situation-data>", instructions, StringComparison.Ordinal);
        Assert.DoesNotContain("<page-data>", instructions, StringComparison.Ordinal);
        Assert.Null(Assert.Single(await UserMessagesAsync(app, id)).ClientContextJson);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_page_at_every_limit_in_text_outside_ascii_is_stored_by_the_endpoint_and_the_service(AssistantTestProvider provider)
    {
        var model = new NhAiScriptedChatClient()
            .RespondWithText("The review is open.")
            .RespondWithText("The review is still open.");
        await using var app = await AssistantWebApplication.StartAsync(database, model, provider: provider);
        using var client = app.CreateClient();
        var id = await CreateConversationAsync(client);
        // Escaped as before, this page needed more characters than the message keeps.
        var page = new NhAssistantClientContextDto(
            WorldText(NhAssistantClientContext.MaxRouteLength),
            WorldText(NhAssistantClientContext.MaxTitleLength),
            Enumerable.Range(1, NhAssistantClientContext.MaxEntities)
                .Select(index => new NhAssistantClientEntityDto(
                    "review-" + new string('a', NhAssistantClientContext.MaxEntityTypeLength - 7),
                    WorldText(NhAssistantClientContext.MaxEntityIdLength - 1) + index,
                    WorldText(NhAssistantClientContext.MaxEntityLabelLength)))
                .ToArray());

        var sent = await SendAsync(app, id, new NhAssistantConversationMessage("What is open?") { ClientContext = page });
        using (var endpoint = await client.PostAsync(
            $"/api/assistant/conversations/{id}/messages",
            Json(new
            {
                text = "And now?",
                clientMessageId = "endpoint-world",
                clientContext = JsonSerializer.SerializeToElement(page, NhAssistantJsonSerializerContext.Default.NhAssistantClientContextDto)
            })))
        {
            Assert.Equal(HttpStatusCode.OK, endpoint.StatusCode);
            await ReadAllAsync(endpoint);
        }

        Assert.True(sent.Success);
        Assert.Equal(NhAssistantTurnStatuses.Completed, sent.Data!.Status);
        Assert.Equal(2, model.Requests.Count);
        Assert.All(model.Requests, request => Assert.Contains("- Route: " + page.Route + "\n", request.Options!.Instructions, StringComparison.Ordinal));
        var stored = await UserMessagesAsync(app, id);
        Assert.Equal(2, stored.Count);
        Assert.All(stored, message =>
        {
            var json = message.ClientContextJson!;
            Assert.InRange(json.Length, 1, NhAssistantClientContext.MaxStorageLength);
            // The text is stored as it is and reads back unchanged.
            Assert.Contains(page.Title!, json, StringComparison.Ordinal);
            var read = NhAssistantClientContext.FromStorage(json)!;
            Assert.Equal((page.Route, page.Title), (read.Route, read.Title));
            Assert.Equal(
                page.Entities!.Select(entity => (entity.Type, entity.Id, entity.Label)),
                read.Entities.Select(entity => (entity.Type, entity.Id, entity.Label)));
        });
        Assert.Equal(stored[0].ClientContextJson, stored[1].ClientContextJson);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_page_the_message_cannot_store_is_ignored_by_the_endpoint_and_refused_by_the_service(AssistantTestProvider provider)
    {
        var model = new NhAiScriptedChatClient().RespondWithText("Nothing I can see.");
        await using var app = await AssistantWebApplication.StartAsync(database, model, provider: provider);
        using var client = app.CreateClient();
        var id = await CreateConversationAsync(client);
        var page = UnstorablePage();

        var refused = await SendAsync(app, id, new NhAssistantConversationMessage("What is open?") { ClientContext = page });
        using (var endpoint = await client.PostAsync(
            $"/api/assistant/conversations/{id}/messages",
            Json(new
            {
                text = "What is open?",
                clientMessageId = "endpoint-unstorable",
                clientContext = JsonSerializer.SerializeToElement(page, NhAssistantJsonSerializerContext.Default.NhAssistantClientContextDto)
            })))
        {
            Assert.Equal(HttpStatusCode.OK, endpoint.StatusCode);
            await ReadAllAsync(endpoint);
        }

        Assert.Equal(NhAssistantErrorCodes.MessageInvalid, CodeOf(refused));
        // The endpoint's turn runs and stores its message without the page, as for one it cannot read.
        var instructions = Assert.Single(model.Requests).Options!.Instructions!;
        Assert.DoesNotContain("<page-data>", instructions, StringComparison.Ordinal);
        Assert.Null(Assert.Single(await UserMessagesAsync(app, id)).ClientContextJson);
        var conversation = await ReadJsonAsync(await client.GetAsync($"/api/assistant/conversations/{id}"));
        Assert.Equal("idle", conversation.GetProperty("status").GetString());
    }

    [Fact]
    public void The_stored_page_keeps_text_outside_ascii_escapes_html_sensitive_text_and_reads_old_rows_alike()
    {
        var page = new NhAssistantClientContext(
            "/projekte/überprüfung",
            "Plan & \"Ziele\" 漢字",
            [new NhAssistantClientEntity("project", "p-ß", "Tom's +1")]);

        var stored = page.ToStorage();
        // Stored before, with every character outside ASCII escaped.
        var old = JsonSerializer.Serialize(
            new
            {
                route = page.Route,
                title = page.Title,
                entities = page.Entities.Select(entity => new { type = entity.Type, id = entity.Id, label = entity.Label })
            },
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.Contains("überprüfung", stored, StringComparison.Ordinal);
        Assert.Contains("漢字", stored, StringComparison.Ordinal);
        Assert.Contains("p-ß", stored, StringComparison.Ordinal);
        Assert.DoesNotContain("&", stored, StringComparison.Ordinal);
        Assert.DoesNotContain("'", stored, StringComparison.Ordinal);
        Assert.DoesNotContain("+", stored, StringComparison.Ordinal);
        Assert.Contains("\\u0026", stored, StringComparison.Ordinal);
        Assert.DoesNotContain("überprüfung", old, StringComparison.Ordinal);
        foreach (var json in new[] { stored, old })
        {
            var read = NhAssistantClientContext.FromStorage(json)!;
            Assert.Equal((page.Route, page.Title), (read.Route, read.Title));
            Assert.Equal(("project", "p-ß", "Tom's +1"), (read.Entities[0].Type, read.Entities[0].Id, read.Entities[0].Label));
        }
    }

    [Fact]
    public void The_reader_ignores_a_page_the_message_cannot_store()
    {
        var unstorable = JsonSerializer.SerializeToElement(UnstorablePage(), NhAssistantJsonSerializerContext.Default.NhAssistantClientContextDto);

        Assert.Null(NhAssistantClientContext.From(unstorable));
    }

    /// <summary>
    /// A page at every limit that still exceeds what the message keeps: <c>&amp;</c> and emoji stay
    /// escaped, six characters each.
    /// </summary>
    private static NhAssistantClientContextDto UnstorablePage()
    {
        return new NhAssistantClientContextDto(
            new string('&', NhAssistantClientContext.MaxRouteLength),
            new string('&', NhAssistantClientContext.MaxTitleLength),
            Enumerable.Range(1, NhAssistantClientContext.MaxEntities)
                .Select(index => new NhAssistantClientEntityDto(
                    "project",
                    new string('&', NhAssistantClientContext.MaxEntityIdLength - 1) + index,
                    string.Concat(Enumerable.Repeat("\U0001F600", NhAssistantClientContext.MaxEntityLabelLength / 2))))
                .ToArray());
    }

    /// <summary>
    /// Text of exactly <paramref name="length"/> characters in several scripts, without white space or
    /// characters the endpoint neutralizes.
    /// </summary>
    private static string WorldText(int length)
    {
        const string Sample = "Überprüfungñéßøå漢字テストΑλφαКирилл";
        return string.Concat(Enumerable.Repeat(Sample, length / Sample.Length + 1))[..length];
    }

    private static async Task<TaskResult<NhAssistantConversationTurnResult>> SendAsync(
        AssistantWebApplication app,
        Guid conversationId,
        NhAssistantConversationMessage message)
    {
        await using var scope = app.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<INhAssistantConversationTurns>().SendAsync(
            Person("user-1"),
            conversationId,
            message,
            new NhAssistantConversationTurnOptions(ChannelCode));
    }

    private static async Task<TaskResult<NhAssistantConversationTurnResult>> DecideAsync(
        AssistantWebApplication app,
        Guid conversationId,
        Guid approvalId,
        string proposalHash)
    {
        await using var scope = app.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<INhAssistantConversationTurns>().DecideAsync(
            Person("user-1"),
            conversationId,
            approvalId,
            new NhAssistantConversationDecision(NhAssistantProposalDecision.Approve, proposalHash),
            new NhAssistantConversationTurnOptions(ChannelCode));
    }

    private static async Task<IReadOnlyList<AssistantMessage>> UserMessagesAsync(AssistantWebApplication app, Guid conversationId)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var messages = await scope.ServiceProvider.GetRequiredService<INhAssistantStore>()
            .GetMessagesAsync(conversationId, 50, CancellationToken.None);
        return messages
            .Where(message => message.Role == NhAssistantMessageRoles.User)
            .OrderBy(message => message.CreatedAt)
            .ToArray();
    }

    /// <summary>
    /// The person as the application's channel authenticated them, with the assistant's access
    /// permission.
    /// </summary>
    private static NhAssistantDecidingPerson Person(string user)
    {
        return new NhAssistantDecidingPerson(
            new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, user), new Claim("permission", "assistant")], ChannelCode)),
            user,
            null);
    }

    private static string? CodeOf(TaskResult result)
    {
        return result.GetResultItems().Select(item => item.Name).FirstOrDefault(name => !string.IsNullOrWhiteSpace(name));
    }

    private static async Task<Guid> CreateConversationAsync(HttpClient client)
    {
        using var created = await client.PostAsync("/api/assistant/conversations", Json(new { agentId = "project-assistant" }));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        return Guid.Parse((await ReadJsonAsync(created)).GetProperty("id").GetString()!);
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }
}
