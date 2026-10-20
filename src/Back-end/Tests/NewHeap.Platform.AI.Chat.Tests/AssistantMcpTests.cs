using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using NewHeap.Platform.AI.Chat.AspNet.Mcp;
using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.AI.Chat.Runtime;
using NewHeap.Platform.AI.Chat.Tests.Infrastructure;
using NewHeap.Platform.AI.Mcp;
using NewHeap.Platform.AI.Test;
using Xunit;

namespace NewHeap.Platform.AI.Chat.Tests;

[Collection(AssistantDatabaseCollection.Name)]
public sealed class AssistantMcpTests(AssistantDatabaseFixture database)
{
    private const string ServerUrl = "http://localhost/planning/mcp";
    private const string LookupFunction = "mcp_planning_lookup_order";
    private const string RescheduleFunction = "mcp_planning_reschedule";

    [Fact]
    public async Task Synced_tools_start_disabled_and_become_visible_to_the_assigned_agent_after_activation()
    {
        var model = new NhAiScriptedChatClient()
            .RespondWithText("No planning tools yet.")
            .RespondWithFunctionCall(LookupFunction, new { order = "A-100" })
            .RespondWithText("Order A-100 is planned for Monday.");
        var servers = new InMemoryMcpServers();
        servers.Register(ServerUrl, () => servers.PlanningTools());
        await using var host = await CreateHostAsync(model, servers);
        await ConnectPlanningServerAsync(host);

        var conversation = await host.CreateConversationAsync();
        await host.SendAsync(conversation.Id, "Which tools do you have?");
        Assert.DoesNotContain(model.Requests[0].Options!.Tools!, tool => tool.Name.StartsWith("mcp_", StringComparison.Ordinal));

        await UpdateToolAsync(host, "Lookup_Order", enabled: true, NhAssistantMcpToolEffects.ReadOnly);
        var turn = await host.SendAsync(conversation.Id, "When is order A-100 planned?");

        Assert.Contains(model.Requests[1].Options!.Tools!, tool => tool.Name == LookupFunction);
        Assert.DoesNotContain(model.Requests[1].Options!.Tools!, tool => tool.Name == RescheduleFunction);
        var completed = turn.Single<NhAssistantToolCompletedEvent>();
        Assert.Equal(NhAssistantToolCallStatuses.Succeeded, completed.Status);
        Assert.Equal("mcp.planning.lookup-order", turn.Single<NhAssistantToolStartedEvent>().ToolId);
        Assert.Equal(["lookup-order"], servers.Calls);
        Assert.Contains(host.Audit.Records, record => record.ToolId == "mcp.planning.lookup-order" && record.Outcome == NhAiOutcomeKind.Succeeded);
    }

    [Fact]
    public async Task An_enabled_mutation_tool_requires_approval_before_the_remote_call()
    {
        var model = new NhAiScriptedChatClient()
            .RespondWithFunctionCall(RescheduleFunction, new { order = "A-100", day = "Friday" })
            .RespondWithText("Order A-100 moved to Friday.");
        var servers = new InMemoryMcpServers();
        servers.Register(ServerUrl, () => servers.PlanningTools());
        await using var host = await CreateHostAsync(model, servers);
        await ConnectPlanningServerAsync(host);
        await UpdateToolAsync(host, "reschedule", enabled: true, NhAssistantMcpToolEffects.Mutation);
        var conversation = await host.CreateConversationAsync();

        var paused = await host.SendAsync(conversation.Id, "Move order A-100 to Friday.");
        var approval = paused.Single<NhAssistantApprovalRequiredEvent>().Approval;
        Assert.Empty(servers.Calls);
        Assert.Equal("mcp.planning.reschedule", approval.ToolId);

        var resumed = await host.DecideAsync(conversation.Id, approval, approve: true);

        Assert.Equal(NhAssistantToolCallStatuses.Succeeded, resumed.Single<NhAssistantToolCompletedEvent>().Status);
        Assert.Equal(["reschedule"], servers.Calls);
    }

    [Fact]
    public async Task An_approved_call_of_a_server_left_out_of_the_turn_reports_why()
    {
        var model = new NhAiScriptedChatClient()
            .RespondWithFunctionCall(RescheduleFunction, new { order = "A-100", day = "Friday" })
            .RespondWithText("Order A-100 moved to Friday.");
        var servers = new InMemoryMcpServers();
        servers.Register(ServerUrl, () => servers.PlanningTools());
        await using var host = await CreateHostAsync(model, servers);
        await ConnectPlanningServerAsync(host);
        await UpdateToolAsync(host, "reschedule", enabled: true, NhAssistantMcpToolEffects.Mutation);
        var conversation = await host.CreateConversationAsync();
        var approval = (await host.SendAsync(conversation.Id, "Move order A-100 to Friday.")).Single<NhAssistantApprovalRequiredEvent>().Approval;

        // Before the decision the server comes to need a policy the person does not pass.
        await using (var context = host.Services.GetRequiredService<NhAssistantDbContextFactory>().CreateDbContext())
        {
            await context.McpServers
                .Where(server => server.Id == "planning")
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(server => server.RequiredPolicy, "app.project.manage")
                    .SetProperty(server => server.UpdatedAt, DateTimeOffset.UtcNow));
        }
        var resumed = await host.DecideAsync(conversation.Id, approval, approve: true);

        // The skip code of the server, as a deferred execution reports it, not a generic invalid approval.
        Assert.Equal(NhAssistantAdminErrorCodes.Forbidden, resumed.Single<NhAssistantErrorEvent>().Code);
        Assert.Empty(servers.Calls);
        var stored = (await host.Services.GetRequiredService<INhAssistantStore>().FindApprovalAsync(approval.ApprovalId, CancellationToken.None))!;
        var invocation = (await host.Services.GetRequiredService<INhAssistantStore>().FindToolInvocationAsync(stored.ToolInvocationId, CancellationToken.None))!;
        Assert.Equal((NhAssistantToolCallStatuses.Failed, NhAssistantAdminErrorCodes.Forbidden), (invocation.Status, invocation.ResultCode));
    }

    [Fact]
    public async Task A_changed_remote_schema_disables_the_tool_until_it_is_activated_again()
    {
        var withPriority = false;
        var servers = new InMemoryMcpServers();
        servers.Register(ServerUrl, () => servers.PlanningTools(withPriority));
        await using var host = await CreateHostAsync(new NhAiScriptedChatClient(), servers);
        await ConnectPlanningServerAsync(host);
        await UpdateToolAsync(host, "reschedule", enabled: true, NhAssistantMcpToolEffects.Mutation);

        withPriority = true;
        var synced = await SyncAsync(host);

        var reschedule = Assert.Single(synced, tool => tool.RemoteName == "reschedule");
        Assert.False(reschedule.IsEnabled);
        Assert.Equal(NhAssistantMcpToolStatuses.SchemaChanged, reschedule.Status);
        var reactivated = await UpdateToolAsync(host, "reschedule", enabled: true, NhAssistantMcpToolEffects.Mutation);
        Assert.Equal(NhAssistantMcpToolStatuses.Available, reactivated.Status);
    }

    [Fact]
    public async Task The_user_token_is_forwarded_only_to_allow_listed_hosts()
    {
        var servers = new InMemoryMcpServers();
        servers.Register(ServerUrl, () => servers.PlanningTools());
        await using var blockedHost = await CreateHostAsync(new NhAiScriptedChatClient(), servers);
        var blocked = await WithAdministrationAsync(blockedHost, async administration =>
        {
            await administration.CreateAsync(Server(NhAssistantMcpAuthModes.ForwardUserToken), "admin-1", CancellationToken.None);
            return await administration.TestAsync("planning", CancellationToken.None);
        });
        Assert.False(blocked.Data!.Ok);
        Assert.Equal(NhAssistantAdminErrorCodes.McpHostBlocked, blocked.Data.Code);
        Assert.Empty(servers.Connections);

        await using var allowedHost = await CreateHostAsync(
            new NhAiScriptedChatClient(),
            servers,
            forwardHosts: ["localhost"]);
        var allowed = await WithAdministrationAsync(allowedHost, async administration =>
        {
            await administration.CreateAsync(Server(NhAssistantMcpAuthModes.ForwardUserToken), "admin-1", CancellationToken.None);
            return await administration.TestAsync("planning", CancellationToken.None);
        });
        Assert.True(allowed.Data!.Ok);
        Assert.Equal(2, allowed.Data.ToolCount);
        var plan = Assert.Single(servers.Connections);
        Assert.Equal("Bearer token-of-admin-1", plan.Headers["Authorization"]);
    }

    [Theory]
    [InlineData("http://169.254.169.254/latest/mcp")]
    [InlineData("https://metadata.google.internal/mcp")]
    [InlineData("http://[fe80::1]/mcp")]
    [InlineData("http://planning.example.org/mcp")]
    public async Task Blocked_hosts_are_rejected(string url)
    {
        await using var host = await CreateHostAsync(new NhAiScriptedChatClient(), new InMemoryMcpServers());

        var created = await WithAdministrationAsync(host, administration => administration.CreateAsync(
            Server(NhAssistantMcpAuthModes.None) with { Url = url },
            "admin-1",
            CancellationToken.None));

        Assert.Equal(NhAssistantAdminErrorCodes.McpHostBlocked, created.GetResultItems()[0].Name);
    }

    [Fact]
    public async Task Secrets_are_protected_and_never_reach_responses_logs_or_audit()
    {
        const string secret = "SECRET-MCP-TOKEN-4711";
        var servers = new InMemoryMcpServers();
        servers.Register(ServerUrl, () => servers.PlanningTools());
        await using var host = await CreateHostAsync(new NhAiScriptedChatClient(), servers);

        await WithAdministrationAsync(host, async administration =>
        {
            await administration.CreateAsync(Server(NhAssistantMcpAuthModes.Bearer) with { Secret = secret }, "admin-1", CancellationToken.None);
            await administration.UpdateAsync(Server(NhAssistantMcpAuthModes.Bearer) with { DisplayName = "Planning 2", Secret = null }, "admin-1", CancellationToken.None);
            return await administration.SyncAsync("planning", "admin-1", CancellationToken.None);
        });

        await using var context = host.Services.GetRequiredService<NhAssistantDbContextFactory>().CreateDbContext();
        var stored = await context.McpServers.SingleAsync();
        Assert.NotNull(stored.ProtectedSecret);
        Assert.DoesNotContain(secret, stored.ProtectedSecret);
        Assert.Equal("Bearer " + secret, servers.Connections.Last().Headers["Authorization"]);
        var captured = string.Join(
            "\n",
            host.Logs.Messages.Concat(host.Business.Events.Select(evt => JsonSerializer.Serialize(evt))));
        Assert.DoesNotContain(secret, captured);
        Assert.Contains(host.Business.Events, evt => evt.Kind == NhAssistantAuditEventKind.AdminMcpServerCreated && evt.ObjectId == "planning");
        Assert.Contains(host.Business.Events, evt => evt.Kind == NhAssistantAuditEventKind.AdminMcpServerSynced);
    }

    [Fact]
    public async Task An_unreachable_server_is_reported_without_breaking_the_turn()
    {
        var model = new NhAiScriptedChatClient()
            .RespondWithFunctionCall("projects_search_v1", new { input = new { query = "roadmap" } })
            .RespondWithText("One project.");
        var servers = new InMemoryMcpServers();
        servers.Register(ServerUrl, () => servers.PlanningTools());
        await using var host = await CreateHostAsync(model, servers);
        await ConnectPlanningServerAsync(host);
        await UpdateToolAsync(host, "Lookup_Order", enabled: true, NhAssistantMcpToolEffects.ReadOnly);
        await WithAdministrationAsync(host, async administration =>
            await administration.UpdateAsync(Server(NhAssistantMcpAuthModes.None) with { Url = "http://localhost:1/mcp" }, "admin-1", CancellationToken.None));
        var conversation = await host.CreateConversationAsync();

        var test = await WithAdministrationAsync(host, administration => administration.TestAsync("planning", CancellationToken.None));
        var turn = await host.SendAsync(conversation.Id, "Search the roadmap.");

        Assert.False(test.Data!.Ok);
        Assert.Equal(NhAssistantAdminErrorCodes.McpUnreachable, test.Data.Code);
        Assert.Equal(NhAssistantTurnStatuses.Completed, Assert.IsType<NhAssistantTurnCompletedEvent>(turn.Events[^1]).Status);
        Assert.DoesNotContain(model.Requests[0].Options!.Tools!, tool => tool.Name.StartsWith("mcp_", StringComparison.Ordinal));
        Assert.Contains(host.Logs.Messages, message => message.Contains("assistant-mcp-unreachable", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_http_client_sends_the_configured_credential_and_maps_unauthorized()
    {
        string? receivedAuthorization = null;
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        await using var remote = builder.Build();
        remote.MapPost("/mcp", (HttpContext context) =>
        {
            receivedAuthorization = context.Request.Headers.Authorization.ToString();
            return Results.StatusCode(StatusCodes.Status401Unauthorized);
        });
        await remote.StartAsync();
        var url = remote.Urls.First().Replace("127.0.0.1", "localhost", StringComparison.Ordinal) + "/mcp";
        await using var host = await CreateHostAsync(new NhAiScriptedChatClient(), mcpFactory: null, forwardHosts: ["localhost"]);

        var result = await WithAdministrationAsync(host, async administration =>
        {
            await administration.CreateAsync(Server(NhAssistantMcpAuthModes.ForwardUserToken) with { Url = url }, "admin-1", CancellationToken.None);
            return await administration.TestAsync("planning", CancellationToken.None);
        });

        Assert.False(result.Data!.Ok);
        Assert.Equal(NhAssistantAdminErrorCodes.McpUnauthorized, result.Data.Code);
        Assert.Equal("Bearer token-of-admin-1", receivedAuthorization);
        await remote.StopAsync();
    }

    private static NhAssistantMcpServerInput Server(string authMode)
    {
        return new NhAssistantMcpServerInput(
            "planning",
            "Planning",
            ServerUrl,
            authMode,
            null,
            null,
            null,
            true);
    }

    [Fact]
    public async Task A_per_caller_tool_list_never_changes_the_reviewed_catalog()
    {
        var model = new NhAiScriptedChatClient()
            .RespondWithText("I cannot look up orders for you.")
            .RespondWithText("The lookup is available again.");
        var listsLookup = true;
        var servers = new InMemoryMcpServers();
        servers.Register(ServerUrl, () => listsLookup
            ? servers.PlanningTools()
            : servers.PlanningTools().Where(tool => tool.ProtocolTool.Name != "Lookup_Order").ToArray());
        await using var host = await CreateHostAsync(model, servers, forwardHosts: ["localhost"]);
        await WithAdministrationAsync(host, async administration =>
        {
            Assert.True((await administration.CreateAsync(Server(NhAssistantMcpAuthModes.ForwardUserToken), "admin-1", CancellationToken.None)).Success);
            return await administration.SyncAsync("planning", "admin-1", CancellationToken.None);
        });
        await UpdateToolAsync(host, "Lookup_Order", enabled: true, NhAssistantMcpToolEffects.ReadOnly);
        await AssignPlanningServerAsync(host);
        var conversation = await host.CreateConversationAsync();

        listsLookup = false;
        var withoutLookup = await host.SendAsync(conversation.Id, "When is order A-100 planned?");

        Assert.Equal(NhAssistantTurnStatuses.Completed, Assert.IsType<NhAssistantTurnCompletedEvent>(withoutLookup.Events[^1]).Status);
        Assert.DoesNotContain(model.Requests[0].Options?.Tools ?? [], tool => tool.Name == LookupFunction);
        var stored = Assert.Single(await ListToolsAsync(host), tool => tool.RemoteName == "Lookup_Order");
        Assert.True(stored.IsEnabled);
        Assert.Equal(NhAssistantMcpToolStatuses.Available, stored.Status);
        Assert.Contains(host.Logs.Messages, message => message.Contains("skipped for this caller (missing)", StringComparison.Ordinal));

        listsLookup = true;
        await host.SendAsync(conversation.Id, "And now?");

        Assert.Contains(model.Requests[1].Options!.Tools!, tool => tool.Name == LookupFunction);
    }

    [Fact]
    public async Task Insufficient_scope_gets_its_own_code_and_is_recorded_as_the_connection_state()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        await using var remote = builder.Build();
        remote.MapPost("/mcp", (HttpContext context) =>
        {
            context.Response.Headers.WWWAuthenticate =
                "Bearer error=\"insufficient_scope\", scope=\"planning:write\", resource_metadata=\"https://planning.example.org/.well-known/oauth-protected-resource\"";
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        });
        await remote.StartAsync();
        var url = remote.Urls.First().Replace("127.0.0.1", "localhost", StringComparison.Ordinal) + "/mcp";
        await using var host = await CreateHostAsync(new NhAiScriptedChatClient(), mcpFactory: null);

        var result = await WithAdministrationAsync(host, async administration =>
        {
            await administration.CreateAsync(
                Server(NhAssistantMcpAuthModes.Bearer) with { Url = url, Secret = "planning-read-token" },
                "admin-1",
                CancellationToken.None);
            return await administration.TestAsync("planning", CancellationToken.None);
        });

        Assert.False(result.Data!.Ok);
        Assert.Equal(NhAssistantAdminErrorCodes.McpInsufficientScope, result.Data.Code);
        var server = await host.Services.GetRequiredService<NhAssistantAdminStore>().FindMcpServerAsync("planning", CancellationToken.None);
        Assert.NotNull(server!.LastConnectAt);
        Assert.Equal(NhAssistantAdminErrorCodes.McpInsufficientScope, server.LastConnectCode);
        Assert.DoesNotContain(host.Logs.Messages, message => message.Contains("planning-read-token", StringComparison.Ordinal));
        await remote.StopAsync();
    }

    [Fact]
    public async Task A_renewable_credential_is_renewed_once_and_each_request_is_repeated_once_after_401()
    {
        var received = new List<string>();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        await using var remote = builder.Build();
        remote.MapPost("/mcp", (HttpContext context) =>
        {
            lock (received)
            {
                received.Add(context.Request.Headers.Authorization.ToString());
            }
            return Results.StatusCode(StatusCodes.Status401Unauthorized);
        });
        await remote.StartAsync();
        var url = remote.Urls.First().Replace("127.0.0.1", "localhost", StringComparison.Ordinal) + "/mcp";
        var renewing = new RenewingCredentialProvider();
        await using var host = await CreateHostAsync(
            new NhAiScriptedChatClient(),
            mcpFactory: null,
            configure: services => services.AddSingleton<INhAssistantMcpCredentialProvider>(renewing));

        var result = await WithAdministrationAsync(host, async administration =>
        {
            await administration.CreateAsync(
                Server(NhAssistantMcpAuthModes.Bearer) with { Url = url, Secret = "unused" },
                "admin-1",
                CancellationToken.None);
            return await administration.TestAsync("planning", CancellationToken.None);
        });

        Assert.False(result.Data!.Ok);
        Assert.Equal(NhAssistantAdminErrorCodes.McpUnauthorized, result.Data.Code);
        Assert.True(renewing.Renewals >= 1);
        Assert.Equal("Bearer expired-token", received[0]);
        Assert.Equal("Bearer renewed-token", received[1]);
        // One repeat per refused request: never a loop of renewals.
        Assert.Equal(2 * renewing.Renewals, received.Count);
        await remote.StopAsync();
    }

    [Fact]
    public async Task Every_request_target_is_checked_against_the_planned_origin_and_the_allowed_hosts()
    {
        await using var restricted = await CreateHostAsync(new NhAiScriptedChatClient(), mcpFactory: null, allowedHosts: ["planning.example.org"]);
        var blockedInner = new RecordingHandler();
        using var blockedClient = CreateHandlerClient(restricted, Server(NhAssistantMcpAuthModes.None), blockedInner);

        var outsideAllowedHosts = await Assert.ThrowsAnyAsync<Exception>(() => blockedClient.PostAsync(ServerUrl, new StringContent("{}")));

        Assert.Equal(NhAssistantAdminErrorCodes.McpHostBlocked, NhAssistantMcpFailures.Classify(outsideAllowedHosts));
        Assert.Empty(blockedInner.Requests);

        await using var open = await CreateHostAsync(new NhAiScriptedChatClient(), mcpFactory: null, allowedHosts: ["localhost"]);
        var protector = open.Services.GetRequiredService<NhAssistantMcpSecretProtector>();
        var inner = new RecordingHandler();
        using var client = CreateHandlerClient(
            open,
            Server(NhAssistantMcpAuthModes.Bearer) with { Secret = null },
            inner,
            protectedSecret: protector.Protect("planning-token"));

        var otherOrigin = await Assert.ThrowsAnyAsync<Exception>(() => client.PostAsync("http://localhost:5555/planning/mcp", new StringContent("{}")));
        using var allowed = await client.PostAsync(ServerUrl, new StringContent("{}"));

        Assert.Equal(NhAssistantAdminErrorCodes.McpHostBlocked, NhAssistantMcpFailures.Classify(otherOrigin));
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        var sent = Assert.Single(inner.Requests);
        Assert.Equal("Bearer planning-token", sent.Authorization);
    }

    [Fact]
    public async Task A_forwarded_token_is_sent_only_for_a_call_authorized_for_the_requesting_person()
    {
        await using var host = await CreateHostAsync(new NhAiScriptedChatClient(), mcpFactory: null, forwardHosts: ["localhost"]);
        await using var scope = host.Services.CreateAsyncScope();
        AssistantTestHost.EnterUser(scope.ServiceProvider, "member-1");
        var provider = scope.ServiceProvider.GetServices<INhAssistantMcpCredentialProvider>()
            .Last(candidate => candidate.Supports(NhAssistantMcpAuthModes.ForwardUserToken));
        var server = new AssistantMcpServer
        {
            Id = "planning",
            Url = ServerUrl,
            AuthMode = NhAssistantMcpAuthModes.ForwardUserToken
        };
        var inner = new RecordingHandler();
        using var client = new HttpClient(new NhAssistantMcpCredentialHandler(
            new NhAssistantMcpCredentialSource(provider, server, NhAssistantMcpCredentialSubject.Request),
            new Uri(ServerUrl),
            host.Services.GetRequiredService<NhAssistantMcpHostGuard>())
        {
            InnerHandler = inner
        });
        var descriptor = new NhAiToolDescriptor(
            "mcp.planning.lookup-order",
            1,
            "Looks up an order.",
            typeof(object),
            typeof(object),
            NhAiToolEffect.ReadOnly,
            NhAiToolExposure.Agent,
            true,
            ["app.assistant.access"]);

        Exception? refused;
        using (NhAiMcpCallContext.Enter("planning", "Lookup_Order", descriptor, Invocation(NhAiGrantSource.Agent)))
        {
            refused = await Record.ExceptionAsync(() => client.PostAsync(ServerUrl, new StringContent("{}")));
        }
        using (NhAiMcpCallContext.Enter("planning", "Lookup_Order", descriptor, Invocation(NhAiGrantSource.Delegated)))
        {
            using var delegated = await client.PostAsync(ServerUrl, new StringContent("{}"));
            Assert.Equal(HttpStatusCode.OK, delegated.StatusCode);
        }
        using var chat = await client.PostAsync(ServerUrl, new StringContent("{}"));

        Assert.NotNull(refused);
        Assert.Equal(NhAssistantAdminErrorCodes.McpCredentialMismatch, NhAssistantMcpFailures.Classify(refused));
        Assert.True(NhAssistantMcpFailures.StoppedBeforeSending(NhAssistantAdminErrorCodes.McpCredentialMismatch));
        Assert.Equal(["Bearer token-of-member-1", "Bearer token-of-member-1"], inner.Requests.Select(request => request.Authorization));
    }

    private static NhAiInvocationContext Invocation(NhAiGrantSource grantSource)
    {
        return new NhAiInvocationContext("assistant-agent:project-assistant", "assistant", new Dictionary<string, string>())
        {
            ActorKind = NhAiActorKind.Agent,
            GrantSource = grantSource
        };
    }

    private static HttpClient CreateHandlerClient(
        AssistantTestHost host,
        NhAssistantMcpServerInput input,
        RecordingHandler inner,
        string? protectedSecret = null)
    {
        var server = new AssistantMcpServer
        {
            Id = input.Id,
            Url = input.Url,
            AuthMode = input.AuthMode,
            ProtectedSecret = protectedSecret
        };
        var provider = new NhAssistantMcpStaticCredentialProvider(host.Services.GetRequiredService<NhAssistantMcpSecretSource>());
        return new HttpClient(new NhAssistantMcpCredentialHandler(
            new NhAssistantMcpCredentialSource(provider, server, NhAssistantMcpCredentialSubject.Request),
            new Uri(input.Url),
            host.Services.GetRequiredService<NhAssistantMcpHostGuard>())
        {
            InnerHandler = inner
        });
    }

    private sealed record SentRequest(Uri? Target, string? Authorization);

    /// <summary>
    /// Answers every request with <c>200</c> and records its target and credential.
    /// </summary>
    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<SentRequest> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(new SentRequest(request.RequestUri, request.Headers.Authorization?.ToString()));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }

    /// <summary>
    /// A bearer credential that expires: the first token is refused and one renewal yields another.
    /// </summary>
    private sealed class RenewingCredentialProvider : INhAssistantMcpCredentialProvider
    {
        private string _token = "expired-token";

        public int Renewals { get; private set; }

        public bool CanRenew => true;

        public bool Supports(string authMode)
        {
            return authMode == NhAssistantMcpAuthModes.Bearer;
        }

        public ValueTask<Common.Models.TaskResult<NhAssistantMcpCredential>> GetAsync(AssistantMcpServer server, NhAssistantMcpCredentialSubject subject, CancellationToken cancellationToken)
        {
            return ValueTask.FromResult(Credential());
        }

        public ValueTask<Common.Models.TaskResult<NhAssistantMcpCredential>> RenewAsync(AssistantMcpServer server, NhAssistantMcpCredentialSubject subject, NhAssistantMcpChallenge challenge, CancellationToken cancellationToken)
        {
            Renewals++;
            _token = "renewed-token";
            return ValueTask.FromResult(Credential());
        }

        public Common.Models.TaskResult AuthorizeCall(AssistantMcpServer server, NhAssistantMcpCredentialSubject subject, NhAiMcpCallContext call)
        {
            return Common.Models.TaskResult.Succeeded();
        }

        private Common.Models.TaskResult<NhAssistantMcpCredential> Credential()
        {
            return Common.Models.TaskResult<NhAssistantMcpCredential>.Succeeded(new NhAssistantMcpCredential(
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Authorization"] = "Bearer " + _token
                }));
        }
    }

    private Task<AssistantTestHost> CreateHostAsync(
        NhAiScriptedChatClient model,
        InMemoryMcpServers? mcpFactory,
        IReadOnlyList<string>? forwardHosts = null,
        IReadOnlyList<string>? allowedHosts = null,
        Action<IServiceCollection>? configure = null)
    {
        return AssistantTestHost.CreateAsync(
            database,
            AssistantTestProvider.PostgreSql,
            model,
            configure: services =>
            {
                if (mcpFactory is not null)
                {
                    services.Replace(ServiceDescriptor.Singleton<INhAssistantMcpClientFactory>(mcpFactory));
                }
                configure?.Invoke(services);
            },
            assistantBuilder: assistant => assistant.ConfigureMcp(mcp =>
            {
                mcp.RequireHttps = true;
                mcp.ForwardUserTokenHosts = forwardHosts?.ToList() ?? [];
                mcp.AllowedHosts = allowedHosts?.ToList() ?? [];
            }));
    }

    private static async Task AssignPlanningServerAsync(AssistantTestHost host)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var agents = scope.ServiceProvider.GetRequiredService<NhAssistantAgentAdministration>();
        var catalog = scope.ServiceProvider.GetRequiredService<NhAssistantAgentCatalog>();
        var agent = (await catalog.FindAsync("project-assistant", includeDisabled: true, CancellationToken.None))!;
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

    private static async Task<IReadOnlyList<Entities.AssistantMcpTool>> ListToolsAsync(AssistantTestHost host)
    {
        var result = await WithAdministrationAsync(host, administration => administration.ListToolsAsync("planning", CancellationToken.None));
        return result.Data!;
    }

    private static async Task ConnectPlanningServerAsync(AssistantTestHost host)
    {
        await WithAdministrationAsync(host, async administration =>
        {
            Assert.True((await administration.CreateAsync(Server(NhAssistantMcpAuthModes.None), "admin-1", CancellationToken.None)).Success);
            return await administration.SyncAsync("planning", "admin-1", CancellationToken.None);
        });
        await using var scope = host.Services.CreateAsyncScope();
        var agents = scope.ServiceProvider.GetRequiredService<NhAssistantAgentAdministration>();
        var catalog = scope.ServiceProvider.GetRequiredService<NhAssistantAgentCatalog>();
        var agent = (await catalog.FindAsync("project-assistant", includeDisabled: true, CancellationToken.None))!;
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

    private static async Task<IReadOnlyList<Entities.AssistantMcpTool>> SyncAsync(AssistantTestHost host)
    {
        var result = await WithAdministrationAsync(host, administration => administration.SyncAsync("planning", "admin-1", CancellationToken.None));
        return result.Data!;
    }

    private static async Task<Entities.AssistantMcpTool> UpdateToolAsync(
        AssistantTestHost host,
        string remoteName,
        bool enabled,
        string effect)
    {
        var result = await WithAdministrationAsync(host, administration =>
            administration.UpdateToolAsync("planning", remoteName, enabled, effect, null, "admin-1", CancellationToken.None));
        Assert.True(result.Success);
        return result.Data!;
    }

    private static async Task<T> WithAdministrationAsync<T>(
        AssistantTestHost host,
        Func<NhAssistantMcpAdministration, Task<T>> action)
    {
        await using var scope = host.Services.CreateAsyncScope();
        AssistantTestHost.EnterUser(scope.ServiceProvider, "admin-1");
        return await action(scope.ServiceProvider.GetRequiredService<NhAssistantMcpAdministration>());
    }
}
