using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.AI.Chat.AspNet.Mcp;
using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.AI.Chat.Runtime;
using NewHeap.Platform.AI.Chat.Tests.Infrastructure;
using NewHeap.Platform.AI.Test;
using Xunit;

namespace NewHeap.Platform.AI.Chat.Tests;

/// <summary>
/// Personal OAuth connections (M4): each person connects their own account, the chat asks a person
/// who has not with a <c>connection-required</c> card that survives a reload, a colleague in a shared
/// conversation works with their own account, step-up asks to connect again, and disconnecting stops
/// the tools for that person only.
/// </summary>
public sealed partial class AssistantMcpOAuthTests
{
    private const string LookupFunction = "mcp_planning_lookup_order";

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task Each_person_connects_their_own_account_and_a_turn_uses_the_connection_of_the_person_who_asked(
        AssistantTestProvider provider)
    {
        var model = new NhAiScriptedChatClient()
            .RespondWithText("Connect Planning first.")
            .RespondWithFunctionCall(LookupFunction, new { order = "A-100" }, "call-ann")
            .RespondWithText("Order A-100 is planned for Monday.")
            .RespondWithText("Connect Planning first, Bob.")
            .RespondWithFunctionCall(LookupFunction, new { order = "A-100" }, "call-bob")
            .RespondWithText("Order A-100 is planned for Monday.")
            .RespondWithText("Planning is not connected any more.");
        await using var authorization = await NhAiTestAuthorizationServer.StartAsync();
        authorization.RegisterClient("assistant-web", WebClientSecret, RedirectUri);
        await using var mcp = await ProtectedMcpServer.StartAsync(authorization);
        await using var host = await CreateHostAsync(provider, model: model);
        await AddPersonalServerAsync(host, mcp.Url, authorization);
        var conversation = await host.CreateConversationAsync();

        // Ann has not connected: the turn goes on without the server and shows the card.
        var unconnected = await host.SendAsync(conversation.Id, "When is order A-100 planned?");
        var card = unconnected.Single<NhAssistantConnectionRequiredEvent>();
        Assert.Equal(("planning", "Planning", NhAssistantMcpConnectionReasons.NotConnected), (card.ServerId, card.DisplayName, card.Reason));
        Assert.DoesNotContain(model.Requests[0].Options!.Tools ?? [], tool => tool.Name.StartsWith("mcp_", StringComparison.Ordinal));
        Assert.Contains("Not connected for this person: Planning", model.Requests[0].Options!.Instructions, StringComparison.Ordinal);
        var stored = (await host.ReadViewAsync(conversation.Id)).Messages.Last().Parts.OfType<NhAssistantConnectionRequiredPartView>().Single();
        Assert.Equal("planning", stored.ServerId);

        authorization.Options.Subject = "remote-ann";
        await McpOAuthTestFlows.ConnectPersonAsync(host, AssistantTestHost.UserId, "planning");
        var connected = await host.SendAsync(conversation.Id, "And now?");

        Assert.Empty(connected.Events.OfType<NhAssistantConnectionRequiredEvent>());
        Assert.Equal(NhAssistantToolCallStatuses.Succeeded, connected.Single<NhAssistantToolCompletedEvent>().Status);
        Assert.Equal("remote-ann", mcp.Resource.Requests.Last().Subject);
        Assert.Contains(host.Business.Events, evt => evt.Kind == NhAssistantAuditEventKind.ToolInvoked
            && evt.ToolId == "mcp.planning.lookup-order"
            && evt.McpConnectionOwner == AssistantTestHost.UserId);

        // A colleague in the shared conversation is asked for their own account, never uses Ann's.
        await host.Services.GetRequiredService<INhAssistantStore>().TryAddParticipantAsync(new AssistantConversationParticipant
        {
            ConversationId = conversation.Id,
            ActorId = "member-2",
            DisplayName = "Bob",
            JoinedVia = NhAssistantParticipantSources.Link,
            JoinedAt = DateTimeOffset.UtcNow
        }, 5, CancellationToken.None);
        var colleague = await host.SendAsync(conversation.Id, "When is order A-100 planned?", userId: "member-2");
        Assert.Equal("planning", colleague.Single<NhAssistantConnectionRequiredEvent>().ServerId);
        Assert.DoesNotContain(model.Requests[3].Options!.Tools ?? [], tool => tool.Name.StartsWith("mcp_", StringComparison.Ordinal));

        authorization.Options.Subject = "remote-bob";
        await McpOAuthTestFlows.ConnectPersonAsync(host, "member-2", "planning");
        await host.SendAsync(conversation.Id, "And now?", userId: "member-2");
        Assert.Equal("remote-bob", mcp.Resource.Requests.Last().Subject);

        // Ann's list shows her connection; disconnecting revokes it and stops the tools for her only.
        var listed = await ListPersonalAsync(host, AssistantTestHost.UserId);
        Assert.Equal(NhAssistantMcpConnectionStatuses.Active, Assert.Single(listed).ConnectionStatus);
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var user = AssistantTestHost.EnterUser(scope.ServiceProvider, AssistantTestHost.UserId).User;
            var removed = await scope.ServiceProvider.GetRequiredService<NhAssistantMcpConnectionService>()
                .DisconnectAsync(AssistantTestHost.UserId, user, "planning", CancellationToken.None);
            Assert.True(removed.Success);
            Assert.Equal(NhAssistantMcpPersonalServer.NotConnected, removed.Data!.ConnectionStatus);
        }
        Assert.Contains(host.Business.Events, evt => evt.Kind == NhAssistantAuditEventKind.McpConnectionDisconnected
            && evt.ActorId == AssistantTestHost.UserId
            && evt.ResultCode == NhAssistantMcpConnectionService.PersonReason);
        Assert.NotEmpty(authorization.Revocations);
        var afterDisconnect = await host.SendAsync(conversation.Id, "And now?");
        Assert.Equal(NhAssistantMcpConnectionReasons.NotConnected, afterDisconnect.Single<NhAssistantConnectionRequiredEvent>().Reason);
        Assert.Equal(NhAssistantMcpConnectionStatuses.Active, (await FindPersonConnectionAsync(host, "member-2"))!.Status);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_server_that_asks_for_more_scopes_asks_the_person_to_connect_again(AssistantTestProvider provider)
    {
        var model = new NhAiScriptedChatClient()
            .RespondWithText("Planning needs more permissions.")
            .RespondWithFunctionCall(LookupFunction, new { order = "A-100" }, "call-2")
            .RespondWithText("Order A-100 is planned for Monday.");
        await using var authorization = await NhAiTestAuthorizationServer.StartAsync();
        authorization.RegisterClient("assistant-web", WebClientSecret, RedirectUri);
        await using var mcp = await ProtectedMcpServer.StartAsync(authorization);
        await using var host = await CreateHostAsync(provider, model: model);
        // Without allowed scopes the authorization server decides; the server asks for more later.
        await AddPersonalServerAsync(host, mcp.Url, authorization, scopes: null);
        await McpOAuthTestFlows.ConnectPersonAsync(host, AssistantTestHost.UserId, "planning");
        var conversation = await host.CreateConversationAsync();

        // The server now refuses the connection's scopes: the turn records them and asks at once.
        mcp.Resource.RequiredScopes = ["mcp:write"];
        var asked = await host.SendAsync(conversation.Id, "When is order A-100 planned?");
        Assert.Equal(NhAssistantMcpConnectionReasons.InsufficientScope, asked.Single<NhAssistantConnectionRequiredEvent>().Reason);
        Assert.Empty(asked.Events.OfType<NhAssistantToolStartedEvent>());
        Assert.Equal("mcp:write", (await FindPersonConnectionAsync(host, AssistantTestHost.UserId))!.RequiredScopes);

        // Connecting again asks for the scopes the server named, and the next turn uses them.
        await McpOAuthTestFlows.ConnectPersonAsync(host, AssistantTestHost.UserId, "planning");
        var reconnected = await FindPersonConnectionAsync(host, AssistantTestHost.UserId);
        Assert.Equal("mcp:write", reconnected!.GrantedScopes);
        Assert.Null(reconnected.RequiredScopes);
        var answered = await host.SendAsync(conversation.Id, "Try again.");
        Assert.Empty(answered.Events.OfType<NhAssistantConnectionRequiredEvent>());
        Assert.Equal(NhAssistantToolCallStatuses.Succeeded, answered.Single<NhAssistantToolCompletedEvent>().Status);
    }

    [Fact]
    public async Task People_connect_and_disconnect_their_own_accounts_over_the_endpoints_and_the_chat_streams_the_card()
    {
        await using var authorization = await NhAiTestAuthorizationServer.StartAsync();
        authorization.RegisterClient("assistant-web", WebClientSecret, RedirectUri);
        await using var mcp = await ProtectedMcpServer.StartAsync(authorization);
        await using var app = await AssistantWebApplication.StartAsync(
            database,
            new NhAiScriptedChatClient().RespondWithText("Connect Planning first."),
            assistantBuilder: assistant => assistant.ConfigureMcp(options => options.OAuth.RedirectUri = RedirectUri));
        using var admin = app.CreateClient("admin-user", admin: true);
        using var member = app.CreateClient("member-1");
        using var browser = app.CreateClient(user: null, access: false);

        using var created = await admin.PostAsync("/api/assistant/admin/mcp-servers", Json(new
        {
            id = "planning",
            displayName = "Planning",
            url = mcp.Url,
            authMode = "oauth",
            isEnabled = true,
            connectionScope = "user",
            oauth = new { clientId = "assistant-web", clientSecret = WebClientSecret, scopes = "mcp:read", confirmAuthorization = true }
        }));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var server = await ReadJsonAsync(created);
        Assert.Equal("user", server.GetProperty("connectionScope").GetString());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, server.GetProperty("organizationConnection").ValueKind);
        // The administrator connects their own account to sync the tools.
        await ConnectOverHttpAsync(admin, browser, "/api/assistant/admin/mcp-servers/planning/connection/authorize", "user");
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync("/api/assistant/admin/mcp-servers/planning/sync", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsync(
            "/api/assistant/admin/mcp-servers/planning/tools/Lookup_Order",
            Json(new { isEnabled = true, effect = "read-only", descriptionOverride = (string?)null }))).StatusCode);
        await AssignToAgentAsync(app.Services);

        var listed = Assert.Single((await ReadJsonAsync(await member.GetAsync("/api/assistant/mcp-connections"))).EnumerateArray());
        Assert.Equal("not-connected", listed.GetProperty("connectionStatus").GetString());
        Assert.False(listed.GetProperty("offersBackgroundUse").GetBoolean());
        using var consent = await member.PutAsync("/api/assistant/mcp-connections/planning", Json(new { allowBackgroundUse = true }));
        Assert.Equal(HttpStatusCode.Conflict, consent.StatusCode);
        Assert.Equal(NhAssistantAdminErrorCodes.McpHeadlessNotAllowed, (await ReadJsonAsync(consent)).GetProperty("code").GetString());

        // A turn without the member's connection streams the card and stores it in the message.
        var conversation = await ReadJsonAsync(await member.PostAsync("/api/assistant/conversations", Json(new { agentId = "project-assistant" })));
        var conversationId = conversation.GetProperty("id").GetString();
        using var turn = await member.PostAsync(
            $"/api/assistant/conversations/{conversationId}/messages",
            Json(new { text = "When is order A-100 planned?", clientMessageId = "c-1" }));
        var (events, _) = await ServerSentEventReader.ReadAllAsync(turn);
        var card = events.Single(evt => evt.Name == "connection.required").Data;
        AssertNames(card, "serverId", "displayName", "reason");
        Assert.Equal("not-connected", card.GetProperty("reason").GetString());
        var reloaded = await ReadJsonAsync(await member.GetAsync($"/api/assistant/conversations/{conversationId}"));
        var part = reloaded.GetProperty("messages").EnumerateArray().Last().GetProperty("parts").EnumerateArray()
            .Single(item => item.GetProperty("type").GetString() == "connection-required");
        AssertNames(part, "type", "serverId", "displayName", "reason");

        // The member connects from the card and disconnects again.
        await ConnectOverHttpAsync(member, browser, "/api/assistant/mcp-connections/planning/authorize", "user");
        var connected = Assert.Single((await ReadJsonAsync(await member.GetAsync("/api/assistant/mcp-connections"))).EnumerateArray());
        Assert.Equal("active", connected.GetProperty("connectionStatus").GetString());
        Assert.Equal("mcp:read", connected.GetProperty("grantedScopes").GetString());
        using var unknown = await member.PostAsync("/api/assistant/mcp-connections/elsewhere/authorize", null);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        using var disconnected = await member.DeleteAsync("/api/assistant/mcp-connections/planning");
        Assert.Equal(HttpStatusCode.NoContent, disconnected.StatusCode);
        Assert.Equal("not-connected", Assert.Single((await ReadJsonAsync(await member.GetAsync("/api/assistant/mcp-connections"))).EnumerateArray())
            .GetProperty("connectionStatus").GetString());
        Assert.Contains("refresh_token", authorization.Revocations);
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_personal_connection_unused_for_the_retention_period_is_removed_and_revoked(AssistantTestProvider provider)
    {
        await using var authorization = await NhAiTestAuthorizationServer.StartAsync();
        authorization.RegisterClient("assistant-web", WebClientSecret, RedirectUri);
        await using var mcp = await ProtectedMcpServer.StartAsync(authorization);
        await using var host = await CreateHostAsync(provider);
        await AddPersonalServerAsync(host, mcp.Url, authorization);
        await McpOAuthTestFlows.ConnectPersonAsync(host, AssistantTestHost.UserId, "planning");
        await McpOAuthTestFlows.ConnectPersonAsync(host, "member-2", "planning");
        var stale = DateTimeOffset.UtcNow.AddDays(-91);
        await using (var context = host.Services.GetRequiredService<NhAssistantDbContextFactory>().CreateDbContext())
        {
            await context.McpConnections
                .Where(row => row.OwnerActorId == AssistantTestHost.UserId || row.OwnerActorId == "member-2")
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(row => row.LastUsedAt, stale)
                    .SetProperty(row => row.ConnectedAt, stale)
                    .SetProperty(row => row.CreatedAt, stale));
            // The second person consented to background use of the server.
            await context.McpConnections
                .Where(row => row.OwnerActorId == "member-2")
                .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.AllowBackgroundUse, true));
        }

        await new NhAssistantMcpMaintenance(
                host.Services.GetRequiredService<IServiceScopeFactory>(),
                host.Services.GetRequiredService<NhAssistantRegistrationState>(),
                Microsoft.Extensions.Logging.Abstractions.NullLogger<NhAssistantMcpMaintenance>.Instance)
            .RunOnceAsync(CancellationToken.None);

        Assert.Null(await FindPersonConnectionAsync(host, AssistantTestHost.UserId));
        // A consent to background use stays without tokens, as after a disconnect.
        var consented = (await FindPersonConnectionAsync(host, "member-2"))!;
        Assert.Equal((null, null, true), (consented.Status, consented.ProtectedRefreshToken, consented.AllowBackgroundUse));
        // The administrator's own connection was used just now and stays.
        Assert.NotNull(await FindPersonConnectionAsync(host, "admin-1"));

        // The administrator reads and removes their own connection of a server each person connects to.
        var own = await AdministerAsync(host, administration => administration.GetConnectionAsync("planning", "admin-1", CancellationToken.None));
        Assert.Equal(NhAssistantMcpConnectionStatuses.Active, own.Data.Connection!.Status);
        Assert.Equal("admin-1", own.Data.Connection.OwnerActorId);
        Assert.True((await AdministerAsync(host, administration => administration.DisconnectOrganizationAsync("planning", "admin-1", CancellationToken.None))).Success);
        Assert.Null((await FindPersonConnectionAsync(host, "admin-1"))?.Status);
        Assert.Null((await AdministerAsync(host, administration => administration.GetConnectionAsync("planning", "admin-1", CancellationToken.None))).Data.Connection);
        Assert.NotEmpty(authorization.Revocations);
        Assert.Contains(host.Business.Events, evt => evt.Kind == NhAssistantAuditEventKind.McpConnectionDisconnected
            && evt.ActorId == AssistantTestHost.UserId
            && evt.ResultCode == NhAssistantMcpOAuthConnections.RetentionReason);
    }

    /// <summary>Starts an authorization, plays the browser and completes it with the client's session.</summary>
    private static async Task ConnectOverHttpAsync(HttpClient client, HttpClient browser, string authorizePath, string ownerKind)
    {
        var start = await ReadJsonAsync(await client.PostAsync(authorizePath, null));
        var redirect = await AuthorizeAsync(start.GetProperty("authorizationUrl").GetString()!);
        var page = await (await browser.GetAsync("/api/assistant/mcp/oauth/callback" + redirect.Query)).Content.ReadAsStringAsync();
        var completionCode = System.Text.RegularExpressions.Regex.Match(page, "\"completionCode\":\"([^\"]+)\"").Groups[1].Value;
        using var completed = await client.PostAsync(
            "/api/assistant/mcp-connections/complete",
            Json(new { flowId = start.GetProperty("flowId").GetString(), completionCode }));
        Assert.Equal(HttpStatusCode.OK, completed.StatusCode);
        Assert.Equal(ownerKind, (await ReadJsonAsync(completed)).GetProperty("ownerKind").GetString());
    }

    private static async Task AssignToAgentAsync(IServiceProvider services)
    {
        await using var scope = services.CreateAsyncScope();
        var agents = scope.ServiceProvider.GetRequiredService<NhAssistantAgentAdministration>();
        var agent = (await scope.ServiceProvider.GetRequiredService<NhAssistantAgentCatalog>().FindAsync("project-assistant", includeDisabled: true, CancellationToken.None))!;
        Assert.True((await agents.UpdateAsync(
            new NhAssistantAgentInput(
                agent.Id,
                agent.Definition.DisplayNameKey,
                agent.Definition.DescriptionKey,
                agent.Definition.Instructions.Content,
                agent.Definition.ToolSelectors,
                ["planning"],
                agent.Definition.RequiredPolicy,
                agent.Definition.Autonomy),
            agent.Definition.Version,
            "admin-1",
            CancellationToken.None)).Success);
    }

    /// <summary>
    /// A server each person connects to, assigned to the test agent with its lookup tool enabled. The
    /// administrator connects their own account to sync it.
    /// </summary>
    private static async Task AddPersonalServerAsync(
        AssistantTestHost host,
        string url,
        NhAiTestAuthorizationServer authorization,
        string? scopes = "mcp:read")
    {
        var input = new NhAssistantMcpServerInput("planning", "Planning", url, NhAssistantMcpAuthModes.OAuth, null, null, null, true)
        {
            ConnectionScope = NhAssistantMcpConnectionScopes.User,
            OAuth = new NhAssistantMcpOAuthInput("assistant-web", WebClientSecret, null, scopes, null, ConfirmAuthorization: true)
        };
        Assert.True((await CreateAsync(host, input)).Success);
        // Without the administrator's own account the server cannot be tested; that says nothing about
        // the server for everyone else, so its connection state stays as it was.
        Assert.Equal(NhAssistantAdminErrorCodes.McpConnectionRequired, (await AdministerAsync(host, administration => administration.TestAsync("planning", "admin-1", CancellationToken.None))).Data!.Code);
        Assert.False((await AdministerAsync(host, administration => administration.SyncAsync("planning", "admin-1", CancellationToken.None))).Success);
        Assert.Null((await FindServerAsync(host)).LastConnectCode);
        var subject = authorization.Options.Subject;
        authorization.Options.Subject = "remote-admin";
        var start = (await AdministerAsync(host, administration => administration.AuthorizeOrganizationAsync("planning", "admin-1", null, CancellationToken.None))).Data!;
        var outcome = await McpOAuthTestFlows.CallbackAsync(host, await McpOAuthTestFlows.AuthorizeAsync(start.AuthorizationUrl));
        var completed = await McpOAuthTestFlows.CompleteAsync(host, start.FlowId, outcome.CompletionCode!, "admin-1", administrator: false);
        Assert.Equal(NhAssistantMcpConnectionOwners.User, completed.Data!.OwnerKind);
        authorization.Options.Subject = subject;
        Assert.True((await AdministerAsync(host, administration => administration.TestAsync("planning", "admin-1", CancellationToken.None))).Data!.Ok);
        Assert.True((await AdministerAsync(host, administration => administration.SyncAsync("planning", "admin-1", CancellationToken.None))).Success);
        Assert.True((await AdministerAsync(host, administration => administration.UpdateToolAsync(
            "planning", "Lookup_Order", true, NhAssistantMcpToolEffects.ReadOnly, null, "admin-1", CancellationToken.None))).Success);
        // A shared organization connection is not involved.
        Assert.Null(await FindOrganizationConnectionAsync(host));

        await using var scope = host.Services.CreateAsyncScope();
        var agents = scope.ServiceProvider.GetRequiredService<NhAssistantAgentAdministration>();
        var agent = (await scope.ServiceProvider.GetRequiredService<NhAssistantAgentCatalog>().FindAsync(AssistantTestData.Agent().Id, includeDisabled: true, CancellationToken.None))!;
        var assigned = await agents.UpdateAsync(
            new NhAssistantAgentInput(
                agent.Id,
                agent.Definition.DisplayNameKey,
                agent.Definition.DescriptionKey,
                agent.Definition.Instructions.Content,
                agent.Definition.ToolSelectors,
                ["planning"],
                agent.Definition.RequiredPolicy,
                agent.Definition.Autonomy),
            agent.Definition.Version,
            "admin-1",
            CancellationToken.None);
        Assert.True(assigned.Success);
    }

    private static async Task<IReadOnlyList<NhAssistantMcpPersonalServer>> ListPersonalAsync(AssistantTestHost host, string actorId)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var user = AssistantTestHost.EnterUser(scope.ServiceProvider, actorId).User;
        return await scope.ServiceProvider.GetRequiredService<NhAssistantMcpConnectionService>().ListAsync(actorId, user, CancellationToken.None);
    }

    private static async Task<AssistantMcpConnection?> FindPersonConnectionAsync(AssistantTestHost host, string actorId)
    {
        await using var scope = host.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<NhAssistantMcpOAuthStore>()
            .FindConnectionAsync("planning", NhAssistantMcpConnectionOwners.User, actorId, string.Empty, CancellationToken.None);
    }
}
