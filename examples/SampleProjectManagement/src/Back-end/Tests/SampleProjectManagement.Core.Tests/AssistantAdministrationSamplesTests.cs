using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using NewHeap.Platform.AI.Chat;
using NewHeap.Platform.AI.Test;
using SampleProjectManagement.Api.Composition;
using Xunit;

namespace SampleProjectManagement.Core.Tests;

/// <summary>
/// Executable evidence for assistant administration and personalization (SPM-250, SPM-251,
/// SPM-252) over the assistant HTTP API of the sample composition.
/// </summary>
public sealed partial class AssistantSamplesTests
{
    [Fact]
    public async Task SPM_250_an_administrator_creates_an_agent_that_users_can_use()
    {
        var model = new NhAiScriptedChatClient()
            .RespondWithFunctionCall("projects_search_v1", new { input = new { query = "portfolio", limit = 5 } })
            .RespondWithText("The portfolio has one roadmap project.");
        Host.Model.Use(model);
        using var admin = Host.CreateClient("spm-250-admin", admin: true);
        using var user = Host.CreateClient("spm-250-user");

        using var created = await admin.PostAsync("/api/assistant/admin/agents", AssistantSampleHost.Json(new
        {
            id = "portfolio-reviewer",
            displayName = "Portfolio reviewer",
            description = "Reviews the project portfolio of the active division.",
            instructions = "Review the project portfolio and summarize it in one sentence.",
            toolSelectors = new[] { "projects.search" },
            mcpServerIds = Array.Empty<string>(),
            requiredPolicy = (string?)null,
            autonomy = "observe",
            isEnabled = true
        }), TestContext.Current.CancellationToken);
        using var forbidden = await user.GetAsync("/api/assistant/admin/agents", TestContext.Current.CancellationToken);
        var agents = await Host.GetJsonAsync(user, "/api/assistant/agents");
        var conversationId = await CreateConversationAsync(user, "portfolio-reviewer");
        var events = await Host.SendAsync(user, conversationId, "Review the portfolio.");

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        Assert.Contains(agents.EnumerateArray(), agent => agent.GetProperty("displayNameKey").GetString() == "Portfolio reviewer");
        Assert.Equal("succeeded", events.Single(evt => evt.Name == "tool.completed").Data.GetProperty("status").GetString());
        Assert.Contains("Review the project portfolio", model.Requests[0].Options!.Instructions);
        Assert.DoesNotContain(model.Requests[0].Options!.Tools!, tool => tool.Name == "projects_change_status_v1");
        Assert.Contains(
            Host.Services.GetRequiredService<SampleAssistantAuditLog>().Events,
            evt => evt.Kind == NhAssistantAuditEventKind.AdminAgentCreated && evt.ObjectId == "portfolio-reviewer");
    }

    [Fact]
    public async Task SPM_250_the_application_decides_how_many_tool_selectors_an_agent_may_list()
    {
        using var admin = Host.CreateClient("spm-250-selectors-admin", admin: true);
        string[] settingsActions = [.. Enumerable.Range(0, 200).Select(index => $"settings.action-{index:000}")];

        using var withinLimit = await admin.PostAsync(
            "/api/assistant/admin/agents",
            AgentWithSelectors("settings-assistant", settingsActions),
            TestContext.Current.CancellationToken);
        using var aboveLimit = await admin.PostAsync(
            "/api/assistant/admin/agents",
            AgentWithSelectors(
                "settings-assistant-too-many",
                [.. Enumerable.Range(0, 257).Select(index => $"settings.a-{index:000}")]),
            TestContext.Current.CancellationToken);

        // The sample raises MaxToolSelectorsPerAgent from 128 to 256 in SampleAssistantComposition.
        Assert.Equal(HttpStatusCode.Created, withinLimit.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, aboveLimit.StatusCode);
    }

    private static HttpContent AgentWithSelectors(string id, string[] toolSelectors)
    {
        return AssistantSampleHost.Json(new
        {
            id,
            displayName = "Settings assistant",
            description = "Changes application settings on request.",
            instructions = "Change application settings when asked.",
            toolSelectors,
            mcpServerIds = Array.Empty<string>(),
            requiredPolicy = (string?)null,
            autonomy = "observe",
            isEnabled = true
        });
    }

    [Fact]
    public async Task SPM_251_an_administrator_connects_an_mcp_server_and_assigns_it_to_one_agent()
    {
        await using var planning = await SampleMcpServer.StartAsync("sample-planning-key");
        using var admin = Host.CreateClient("spm-251-admin", admin: true);
        using var user = Host.CreateClient("spm-251-user");

        using var server = await admin.PostAsync("/api/assistant/admin/mcp-servers", AssistantSampleHost.Json(new
        {
            id = "planning",
            displayName = "Planning",
            url = planning.Url,
            authMode = "api-key",
            headerName = (string?)null,
            secret = "sample-planning-key",
            requiredPolicy = (string?)null,
            isEnabled = true
        }), TestContext.Current.CancellationToken);
        using var sync = await admin.PostAsync("/api/assistant/admin/mcp-servers/planning/sync", null, TestContext.Current.CancellationToken);
        var synced = await AssistantSampleHost.ReadJsonAsync(sync);
        using var enable = await admin.PutAsync(
            "/api/assistant/admin/mcp-servers/planning/tools/next_milestone",
            AssistantSampleHost.Json(new { isEnabled = true, effect = "read-only", descriptionOverride = (string?)null }),
            TestContext.Current.CancellationToken);
        using var agent = await admin.PostAsync("/api/assistant/admin/agents", AssistantSampleHost.Json(new
        {
            id = "planning-helper",
            displayName = "Planning helper",
            description = "Answers milestone questions.",
            instructions = "Answer milestone questions with the planning tool.",
            toolSelectors = new[] { "projects.search" },
            mcpServerIds = new[] { "planning" },
            requiredPolicy = (string?)null,
            autonomy = "observe",
            isEnabled = true
        }), TestContext.Current.CancellationToken);

        var model = new NhAiScriptedChatClient()
            .RespondWithFunctionCall("mcp_planning_next_milestone", new { project = "PRJ-1" })
            .RespondWithText("PRJ-1 reaches its next milestone on Friday.")
            .RespondWithText("I can search projects.");
        Host.Model.Use(model);
        var helperConversation = await CreateConversationAsync(user, "planning-helper");
        var helperEvents = await Host.SendAsync(user, helperConversation, "When is the next milestone of PRJ-1?");
        var otherConversation = await Host.CreateConversationAsync(user);
        await Host.SendAsync(user, otherConversation, "Which tools do you have?");

        Assert.Equal(HttpStatusCode.Created, server.StatusCode);
        Assert.False(synced.EnumerateArray().Single().GetProperty("isEnabled").GetBoolean());
        Assert.Equal(HttpStatusCode.OK, enable.StatusCode);
        Assert.Equal(HttpStatusCode.Created, agent.StatusCode);
        var started = helperEvents.Single(evt => evt.Name == "tool.started").Data;
        Assert.Equal("mcp.planning.next-milestone", started.GetProperty("toolId").GetString());
        Assert.Equal("succeeded", helperEvents.Single(evt => evt.Name == "tool.completed").Data.GetProperty("status").GetString());
        Assert.Equal(["PRJ-1"], planning.Calls);
        Assert.All(planning.ApiKeys, key => Assert.Equal("sample-planning-key", key));
        Assert.DoesNotContain(model.Requests[^1].Options!.Tools!, tool => tool.Name == "mcp_planning_next_milestone");
    }

    /// <summary>
    /// The sample bounds every request to an MCP server in its composition: connecting and listing the
    /// tools by the connect timeout, a reader's call by the reader call timeout (SPM-305), what one
    /// response may hold and how many pages and tools a tool list may have.
    /// </summary>
    [Fact]
    public void SPM_251_the_composition_bounds_every_mcp_request()
    {
        var mcp = Host.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<NhAssistantOptions>>().Value.Mcp;

        Assert.Equal(TimeSpan.FromSeconds(10), mcp.ConnectTimeout);
        Assert.Equal(TimeSpan.FromSeconds(60), mcp.ReaderCallTimeout);
        Assert.Equal((16 * 1024 * 1024, 20, 500), (mcp.MaxResponseBytes, mcp.MaxToolListPages, mcp.MaxTools));
    }

    [Fact]
    public async Task SPM_251_the_application_adds_an_authentication_mode_that_exchanges_the_persons_token()
    {
        // SPM-251: an authentication mode of the application's own (D1). The sample registers
        // SampleTokenExchangeCredentialProvider with AddMcpCredentialProvider; administrators choose
        // it like a built-in mode, and every request to the server carries a token the identity
        // provider exchanged for the person, never the person's own token.
        await using var exchange = await SampleTokenExchangeService.StartAsync("spm-251-exchange-client", "spm-251-exchange-secret");
        await using var reports = await SampleMcpServer.StartAsync(request =>
            request.Headers.Authorization.ToString().StartsWith("Bearer exchanged:", StringComparison.Ordinal));
        var configuration = Host.Services.GetRequiredService<IConfiguration>();
        configuration[SampleTokenExchangeCredentialProvider.EndpointKey] = exchange.TokenEndpoint;
        configuration[SampleTokenExchangeCredentialProvider.ClientIdKey] = "spm-251-exchange-client";
        // As the sample's secrets.json provides it.
        configuration[$"{NewHeap.Platform.Common.NhConfigurationSecrets.Prefix}:{SampleTokenExchangeCredentialProvider.ClientSecretKey}"] = "spm-251-exchange-secret";
        const string adminToken = "spm-251-admin-newheap-token";
        const string userToken = "spm-251-user-newheap-token";
        using var admin = Host.CreateClient("spm-251-exchange-admin", admin: true);
        admin.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", adminToken);
        using var user = Host.CreateClient("spm-251-exchange-user");
        user.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", userToken);

        try
        {
            var modes = await Host.GetJsonAsync(admin, "/api/assistant/admin/mcp-auth-modes");
            var mode = Assert.Single(modes.EnumerateArray());
            Assert.Equal(SampleTokenExchangeCredentialProvider.Mode, mode.GetProperty("mode").GetString());
            Assert.Equal("person", mode.GetProperty("ownership").GetString());

            using var created = await admin.PostAsync("/api/assistant/admin/mcp-servers", AssistantSampleHost.Json(new
            {
                id = "reports",
                displayName = "Project reports",
                url = reports.Url,
                authMode = SampleTokenExchangeCredentialProvider.Mode,
                headerName = (string?)null,
                requiredPolicy = (string?)null,
                isEnabled = true
            }), TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            // The administrator synchronizes with their own exchanged token.
            using var synced = await admin.PostAsync("/api/assistant/admin/mcp-servers/reports/sync", null, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, synced.StatusCode);
            using var enabled = await admin.PutAsync(
                "/api/assistant/admin/mcp-servers/reports/tools/next_milestone",
                AssistantSampleHost.Json(new { isEnabled = true, effect = "read-only", descriptionOverride = (string?)null }),
                TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, enabled.StatusCode);
            using var agent = await admin.PostAsync("/api/assistant/admin/agents", AssistantSampleHost.Json(new
            {
                id = "reports-helper",
                displayName = "Reports helper",
                description = "Answers milestone questions from the project reports.",
                instructions = "Answer milestone questions with the reports tool.",
                toolSelectors = new[] { "projects.search" },
                mcpServerIds = new[] { "reports" },
                requiredPolicy = (string?)null,
                autonomy = "observe",
                isEnabled = true
            }), TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.Created, agent.StatusCode);

            Host.Model.Use(new NhAiScriptedChatClient()
                .RespondWithFunctionCall("mcp_reports_next_milestone", new { project = "PRJ-7" })
                .RespondWithText("PRJ-7 reaches its next milestone on Friday."));
            var conversation = await CreateConversationAsync(user, "reports-helper");
            var events = await Host.SendAsync(user, conversation, "When is the next milestone of PRJ-7?");

            Assert.Equal("succeeded", events.Single(evt => evt.Name == "tool.completed").Data.GetProperty("status").GetString());
            Assert.Equal(["PRJ-7"], reports.Calls);
            // The server saw only exchanged tokens: the administrator's while syncing, the person's in the chat.
            Assert.All(reports.Authorizations, authorization => Assert.StartsWith("Bearer exchanged:", authorization, StringComparison.Ordinal));
            Assert.DoesNotContain(reports.Authorizations, authorization => authorization.Contains(userToken, StringComparison.Ordinal) || authorization.Contains(adminToken, StringComparison.Ordinal));
            Assert.Contains("Bearer exchanged:" + SampleTokenExchangeService.HashOf(userToken), reports.Authorizations);
            Assert.Contains(exchange.Exchanges, item => item.SubjectHash == SampleTokenExchangeService.HashOf(adminToken));
            Assert.All(exchange.Exchanges, item => Assert.Equal(reports.Url, item.Resource));
        }
        finally
        {
            using var deletedAgent = await admin.DeleteAsync("/api/assistant/admin/agents/reports-helper", TestContext.Current.CancellationToken);
            using var deletedServer = await admin.DeleteAsync("/api/assistant/admin/mcp-servers/reports", TestContext.Current.CancellationToken);
            Host.Model.Use(new SampleAssistantChatClient());
        }
    }

    [Fact]
    public async Task SPM_251_mcp_secrets_can_stay_with_the_application_and_basic_authentication_sends_a_user_name()
    {
        // SPM-251: an administrator names a secret that the application holds instead of storing
        // one. config:<name> reads NewHeap:AI:Assistant:Mcp:Secrets:<name>, and secret-file:<name>
        // reads a mounted secret file through the sample's resolver. Both are read for every
        // connection, so a rotated secret applies at once; a reference never reaches other settings.
        var configuration = Host.Services.GetRequiredService<IConfiguration>();
        var secrets = Directory.CreateTempSubdirectory("spm-251-secrets-");
        var basic = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("planner:spm-251-basic-password"));
        await using var planning = await SampleMcpServer.StartAsync(request =>
            request.Headers["X-Api-Key"].ToString() is "spm-251-configured-key" or "spm-251-mounted-key" or "spm-251-rotated-key"
            || request.Headers.Authorization.ToString() == basic);
        using var admin = Host.CreateClient("spm-251-secrets-admin", admin: true);
        var server = new
        {
            id = "secured-planning",
            displayName = "Secured planning",
            url = planning.Url,
            authMode = "api-key",
            headerName = (string?)null,
            requiredPolicy = (string?)null,
            isEnabled = true,
            secretReference = "config:spm-251-key"
        };
        try
        {
            configuration[NhAssistantMcpSecretReferences.ConfigurationSection + ":spm-251-key"] = "spm-251-configured-key";
            configuration[SampleMountedSecretResolver.DirectoryKey] = secrets.FullName;
            await File.WriteAllTextAsync(Path.Combine(secrets.FullName, "spm-251-key"), "spm-251-mounted-key", TestContext.Current.CancellationToken);

            using var created = await admin.PostAsync("/api/assistant/admin/mcp-servers", AssistantSampleHost.Json(server), TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            var stored = await AssistantSampleHost.ReadJsonAsync(created);
            Assert.False(stored.GetProperty("hasSecret").GetBoolean());
            Assert.Equal("config:spm-251-key", stored.GetProperty("secretReference").GetString());
            Assert.True(await TestAsync(admin, "secured-planning"));
            Assert.Equal("spm-251-configured-key", planning.ApiKeys.Last());

            using var mounted = await admin.PutAsync(
                "/api/assistant/admin/mcp-servers/secured-planning",
                AssistantSampleHost.Json(server with { secretReference = "secret-file:spm-251-key" }),
                TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, mounted.StatusCode);
            Assert.True(await TestAsync(admin, "secured-planning"));
            Assert.Equal("spm-251-mounted-key", planning.ApiKeys.Last());

            // The platform rotates the mounted file; the next connection sends the new key.
            await File.WriteAllTextAsync(Path.Combine(secrets.FullName, "spm-251-key"), "spm-251-rotated-key", TestContext.Current.CancellationToken);
            Assert.True(await TestAsync(admin, "secured-planning"));
            Assert.Equal("spm-251-rotated-key", planning.ApiKeys.Last());

            using var otherSetting = await admin.PutAsync(
                "/api/assistant/admin/mcp-servers/secured-planning",
                AssistantSampleHost.Json(server with { secretReference = "config:ConnectionStrings:DefaultConnection" }),
                TestContext.Current.CancellationToken);
            using var climbing = await admin.PutAsync(
                "/api/assistant/admin/mcp-servers/secured-planning",
                AssistantSampleHost.Json(server with { secretReference = "secret-file:../spm-251-key" }),
                TestContext.Current.CancellationToken);
            using var missing = await admin.PutAsync(
                "/api/assistant/admin/mcp-servers/secured-planning",
                AssistantSampleHost.Json(server with { secretReference = "secret-file:spm-251-missing" }),
                TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.BadRequest, otherSetting.StatusCode);
            Assert.Equal("invalid", (await AssistantSampleHost.ReadJsonAsync(otherSetting)).GetProperty("errors").GetProperty("secretReference")[0].GetString());
            Assert.Equal(HttpStatusCode.BadRequest, climbing.StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
            // A reference that names no secret is refused at its field.
            Assert.Equal("not-found", (await AssistantSampleHost.ReadJsonAsync(missing)).GetProperty("errors").GetProperty("secretReference")[0].GetString());

            // Basic authentication: the user name is shown, the password is a write-only secret that
            // replaces the reference.
            using var withBasic = await admin.PutAsync("/api/assistant/admin/mcp-servers/secured-planning", AssistantSampleHost.Json(new
            {
                server.id,
                server.displayName,
                server.url,
                authMode = "basic",
                userName = "planner",
                secret = "spm-251-basic-password",
                server.requiredPolicy,
                server.isEnabled
            }), TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, withBasic.StatusCode);
            var basicServer = await AssistantSampleHost.ReadJsonAsync(withBasic);
            Assert.Equal("planner", basicServer.GetProperty("userName").GetString());
            Assert.True(basicServer.GetProperty("hasSecret").GetBoolean());
            Assert.Equal(JsonValueKind.Null, basicServer.GetProperty("secretReference").ValueKind);
            Assert.True(await TestAsync(admin, "secured-planning"));
            Assert.Equal(basic, planning.Authorizations.Last());

            var listed = await (await admin.GetAsync("/api/assistant/admin/mcp-servers", TestContext.Current.CancellationToken)).Content
                .ReadAsStringAsync(TestContext.Current.CancellationToken);
            Assert.DoesNotContain("spm-251-basic-password", listed);
            Assert.DoesNotContain("spm-251-mounted-key", listed);
            Assert.DoesNotContain("spm-251-configured-key", listed);
        }
        finally
        {
            using var deleted = await admin.DeleteAsync("/api/assistant/admin/mcp-servers/secured-planning", TestContext.Current.CancellationToken);
            configuration[SampleMountedSecretResolver.DirectoryKey] = null;
            secrets.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task SPM_299_an_administrator_connects_an_oauth_server_with_the_hosts_client_credentials()
    {
        var cancellation = TestContext.Current.CancellationToken;
        await using var authorization = await NhAiTestAuthorizationServer.StartAsync();
        authorization.RegisterClient("sample-assistant", "sample-spm-299-client-secret");
        await using var planning = await SampleMcpServer.StartProtectedAsync(authorization, resource => resource.ChallengeScope = "planning:read");
        using var admin = Host.CreateClient("spm-299-admin", admin: true);
        using var user = Host.CreateClient("spm-299-user");

        // The administrator reads which authorization server protects the address before trusting it.
        using var discovered = await admin.PostAsync(
            "/api/assistant/admin/mcp-servers/discover",
            AssistantSampleHost.Json(new { url = planning.Url }),
            cancellation);
        var discovery = await AssistantSampleHost.ReadJsonAsync(discovered);
        using var unconfirmed = await admin.PostAsync("/api/assistant/admin/mcp-servers", AssistantSampleHost.Json(new
        {
            id = "oauth-planning",
            displayName = "Planning (OAuth)",
            url = planning.Url,
            authMode = "oauth-client-credentials",
            isEnabled = true,
            oauth = new { clientId = "sample-assistant", clientSecret = "sample-spm-299-client-secret", scopes = "planning:read" }
        }), cancellation);
        // Saving with confirmAuthorization pins that authorization server: tokens are requested there only.
        using var created = await admin.PostAsync("/api/assistant/admin/mcp-servers", AssistantSampleHost.Json(new
        {
            id = "oauth-planning",
            displayName = "Planning (OAuth)",
            url = planning.Url,
            authMode = "oauth-client-credentials",
            isEnabled = true,
            oauth = new { clientId = "sample-assistant", clientSecret = "sample-spm-299-client-secret", scopes = "planning:read", confirmAuthorization = true }
        }), cancellation);
        var server = await AssistantSampleHost.ReadJsonAsync(created);
        using var sync = await admin.PostAsync("/api/assistant/admin/mcp-servers/oauth-planning/sync", null, cancellation);
        using var enable = await admin.PutAsync(
            "/api/assistant/admin/mcp-servers/oauth-planning/tools/next_milestone",
            AssistantSampleHost.Json(new { isEnabled = true, effect = "read-only", descriptionOverride = (string?)null }),
            cancellation);
        using var agent = await admin.PostAsync("/api/assistant/admin/agents", AssistantSampleHost.Json(new
        {
            id = "oauth-planning-helper",
            displayName = "OAuth planning helper",
            description = "Answers milestone questions from the protected planning server.",
            instructions = "Answer milestone questions with the planning tool.",
            toolSelectors = new[] { "projects.search" },
            mcpServerIds = new[] { "oauth-planning" },
            requiredPolicy = (string?)null,
            autonomy = "observe",
            isEnabled = true
        }), cancellation);
        Host.Model.Use(new NhAiScriptedChatClient()
            .RespondWithFunctionCall("mcp_oauth_planning_next_milestone", new { project = "PRJ-1" })
            .RespondWithText("PRJ-1 reaches its next milestone on Friday."));
        var conversation = await CreateConversationAsync(user, "oauth-planning-helper");
        var events = await Host.SendAsync(user, conversation, "When is the next milestone of PRJ-1?");
        var connection = await Host.GetJsonAsync(admin, "/api/assistant/admin/mcp-servers/oauth-planning/connection");
        var listed = await (await admin.GetAsync("/api/assistant/admin/mcp-servers", cancellation)).Content.ReadAsStringAsync(cancellation);
        using var disconnected = await admin.DeleteAsync("/api/assistant/admin/mcp-servers/oauth-planning/connection", cancellation);

        Assert.Equal(authorization.Issuer, discovery.GetProperty("issuer").GetString());
        Assert.Equal("planning:read", discovery.GetProperty("challengedScopes")[0].GetString());
        Assert.Equal(HttpStatusCode.BadRequest, unconfirmed.StatusCode);
        Assert.Equal("required", (await AssistantSampleHost.ReadJsonAsync(unconfirmed)).GetProperty("errors").GetProperty("oauth.confirmAuthorization")[0].GetString());
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal(authorization.Issuer, server.GetProperty("authorization").GetProperty("issuer").GetString());
        Assert.True(server.GetProperty("oauth").GetProperty("hasClientSecret").GetBoolean());
        Assert.Equal(HttpStatusCode.OK, sync.StatusCode);
        Assert.Equal(HttpStatusCode.OK, enable.StatusCode);
        Assert.Equal(HttpStatusCode.Created, agent.StatusCode);
        Assert.Equal("succeeded", events.Single(evt => evt.Name == "tool.completed").Data.GetProperty("status").GetString());
        Assert.Equal(["PRJ-1"], planning.Calls);
        // One token for the server's own address, reused for the sync and the turn.
        var tokenRequest = Assert.Single(authorization.TokenRequests);
        Assert.Equal("client_credentials", tokenRequest.GrantType);
        Assert.Equal(planning.Url, tokenRequest.Resource);
        Assert.All(planning.Resource!.Requests, request => Assert.Equal("sample-assistant", request.Subject));
        Assert.Equal("active", connection.GetProperty("status").GetString());
        Assert.Equal("client-credentials", connection.GetProperty("connectedBy").GetString());
        Assert.DoesNotContain("sample-spm-299-client-secret", listed);
        Assert.Equal(HttpStatusCode.NoContent, disconnected.StatusCode);
        Assert.Contains("access_token", authorization.Revocations);
    }

    [Fact]
    public async Task SPM_299_an_administrator_connects_the_organizations_account_and_the_agent_acts_as_that_account()
    {
        var cancellation = TestContext.Current.CancellationToken;
        await using var authorization = await NhAiTestAuthorizationServer.StartAsync(options => options.Subject = "planning-service-account");
        authorization.RegisterClient("sample-assistant-web", "sample-spm-299-web-secret", AssistantSampleHost.OAuthRedirectUri);
        await using var planning = await SampleMcpServer.StartProtectedAsync(authorization);
        using var admin = Host.CreateClient("spm-299-org-admin", admin: true);
        using var user = Host.CreateClient("spm-299-org-user");
        using var browser = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = admin.BaseAddress };

        using var created = await admin.PostAsync("/api/assistant/admin/mcp-servers", AssistantSampleHost.Json(new
        {
            id = "planning-account",
            displayName = "Planning (organization account)",
            url = planning.Url,
            authMode = "oauth",
            connectionScope = "organization",
            isEnabled = true,
            oauth = new
            {
                clientId = "sample-assistant-web",
                clientSecret = "sample-spm-299-web-secret",
                scopes = "planning:read",
                // Everyone using the server's agents acts as this account; the label says which one.
                organizationAccountLabel = "Planning service account",
                confirmAuthorization = true
            }
        }), cancellation);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.False(await TestAsync(admin, "planning-account"));

        // The administrator's browser opens the authorization URL; the authorization server redirects
        // to the assistant's callback, whose page hands a one-time completion code to the opener.
        using var started = await admin.PostAsync("/api/assistant/admin/mcp-servers/planning-account/connection/authorize", null, cancellation);
        var start = await AssistantSampleHost.ReadJsonAsync(started);
        using var authorize = await browser.GetAsync(start.GetProperty("authorizationUrl").GetString(), cancellation);
        var redirect = authorize.Headers.Location!;
        Assert.StartsWith(AssistantSampleHost.OAuthRedirectUri, redirect.ToString(), StringComparison.Ordinal);
        using var callback = await browser.GetAsync("/api/assistant/mcp/oauth/callback" + redirect.Query, cancellation);
        var page = await callback.Content.ReadAsStringAsync(cancellation);
        var completionCode = System.Text.RegularExpressions.Regex.Match(page, "\"completionCode\":\"([^\"]+)\"").Groups[1].Value;
        // The code alone completes nothing: the starter completes with their own session.
        using var byUser = await user.PostAsync(
            "/api/assistant/mcp-connections/complete",
            AssistantSampleHost.Json(new { flowId = start.GetProperty("flowId").GetString(), completionCode }),
            cancellation);
        using var completed = await admin.PostAsync(
            "/api/assistant/mcp-connections/complete",
            AssistantSampleHost.Json(new { flowId = start.GetProperty("flowId").GetString(), completionCode }),
            cancellation);

        Assert.Equal(HttpStatusCode.OK, callback.StatusCode);
        Assert.Contains("frame-ancestors 'none'", callback.Headers.GetValues("Content-Security-Policy").Single(), StringComparison.Ordinal);
        Assert.NotEmpty(completionCode);
        Assert.Equal(HttpStatusCode.BadRequest, byUser.StatusCode);
        Assert.Equal(HttpStatusCode.OK, completed.StatusCode);
        Assert.Equal("active", (await AssistantSampleHost.ReadJsonAsync(completed)).GetProperty("status").GetString());

        using var sync = await admin.PostAsync("/api/assistant/admin/mcp-servers/planning-account/sync", null, cancellation);
        using var enable = await admin.PutAsync(
            "/api/assistant/admin/mcp-servers/planning-account/tools/next_milestone",
            AssistantSampleHost.Json(new { isEnabled = true, effect = "read-only", descriptionOverride = (string?)null }),
            cancellation);
        using var agent = await admin.PostAsync("/api/assistant/admin/agents", AssistantSampleHost.Json(new
        {
            id = "planning-account-helper",
            displayName = "Planning account helper",
            description = "Answers milestone questions as the planning service account.",
            instructions = "Answer milestone questions with the planning tool.",
            toolSelectors = new[] { "projects.search" },
            mcpServerIds = new[] { "planning-account" },
            requiredPolicy = (string?)null,
            autonomy = "observe",
            isEnabled = true
        }), cancellation);
        Host.Model.Use(new NhAiScriptedChatClient()
            .RespondWithFunctionCall("mcp_planning_account_next_milestone", new { project = "PRJ-2" })
            .RespondWithText("PRJ-2 reaches its next milestone on Friday."));
        var conversation = await CreateConversationAsync(user, "planning-account-helper");
        var events = await Host.SendAsync(user, conversation, "When is the next milestone of PRJ-2?");
        var listed = await Host.GetJsonAsync(admin, "/api/assistant/admin/mcp-servers");
        using var disconnected = await admin.DeleteAsync("/api/assistant/admin/mcp-servers/planning-account/connection", cancellation);

        Assert.Equal(HttpStatusCode.OK, sync.StatusCode);
        Assert.Equal(HttpStatusCode.OK, enable.StatusCode);
        Assert.Equal(HttpStatusCode.Created, agent.StatusCode);
        Assert.Equal("succeeded", events.Single(evt => evt.Name == "tool.completed").Data.GetProperty("status").GetString());
        Assert.Equal(["PRJ-2"], planning.Calls);
        // The user asked, the planning server saw the organization's account.
        Assert.All(planning.Resource!.Requests, request => Assert.Equal("planning-service-account", request.Subject));
        var connection = listed.EnumerateArray().Single(item => item.GetProperty("id").GetString() == "planning-account").GetProperty("organizationConnection");
        Assert.Equal("active", connection.GetProperty("status").GetString());
        Assert.Equal("spm-299-org-admin", connection.GetProperty("connectedBy").GetString());
        Assert.Equal(HttpStatusCode.NoContent, disconnected.StatusCode);
        Assert.Contains("refresh_token", authorization.Revocations);
        Assert.False(await TestAsync(admin, "planning-account"));
    }

    [Fact]
    public async Task SPM_300_a_person_connects_their_own_account_from_the_chat_and_the_agent_works_with_it()
    {
        var cancellation = TestContext.Current.CancellationToken;
        await using var authorization = await NhAiTestAuthorizationServer.StartAsync(options => options.Subject = "remote-admin");
        authorization.RegisterClient("sample-assistant-people", "sample-spm-300-client-secret", AssistantSampleHost.OAuthRedirectUri);
        await using var planning = await SampleMcpServer.StartProtectedAsync(authorization);
        using var admin = Host.CreateClient("spm-300-admin", admin: true);
        using var ann = Host.CreateClient("spm-300-ann");
        using var bob = Host.CreateClient("spm-300-bob");
        using var browser = SampleMcpOAuthBrowser.Create(admin);

        // Each person connects their own planning account; nobody shares one.
        using var created = await admin.PostAsync("/api/assistant/admin/mcp-servers", AssistantSampleHost.Json(new
        {
            id = "personal-planning",
            displayName = "My planning",
            url = planning.Url,
            authMode = "oauth",
            connectionScope = "user",
            isEnabled = true,
            oauth = new { clientId = "sample-assistant-people", clientSecret = "sample-spm-300-client-secret", scopes = "planning:read", confirmAuthorization = true }
        }), cancellation);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        // The administrator connects their own account to synchronize the tools.
        await SampleMcpOAuthBrowser.ConnectAsync(admin, browser, "/api/assistant/admin/mcp-servers/personal-planning/connection/authorize");
        using var sync = await admin.PostAsync("/api/assistant/admin/mcp-servers/personal-planning/sync", null, cancellation);
        using var enable = await admin.PutAsync(
            "/api/assistant/admin/mcp-servers/personal-planning/tools/next_milestone",
            AssistantSampleHost.Json(new { isEnabled = true, effect = "read-only", descriptionOverride = (string?)null }),
            cancellation);
        using var agent = await admin.PostAsync("/api/assistant/admin/agents", AssistantSampleHost.Json(new
        {
            id = "my-planning-helper",
            displayName = "My planning helper",
            description = "Answers milestone questions from each person's own planning account.",
            instructions = "Answer milestone questions with the planning tool.",
            toolSelectors = new[] { "projects.search" },
            mcpServerIds = new[] { "personal-planning" },
            requiredPolicy = (string?)null,
            autonomy = "observe",
            isEnabled = true
        }), cancellation);
        Assert.Equal(HttpStatusCode.OK, sync.StatusCode);
        Assert.Equal(HttpStatusCode.OK, enable.StatusCode);
        Assert.Equal(HttpStatusCode.Created, agent.StatusCode);

        Host.Model.Use(new NhAiScriptedChatClient()
            .RespondWithText("Connect My planning first.")
            .RespondWithFunctionCall("mcp_personal_planning_next_milestone", new { project = "PRJ-3" })
            .RespondWithText("PRJ-3 reaches its next milestone on Friday.")
            .RespondWithText("Connect My planning first."));

        // Ann has not connected: the answer carries a card that survives a reload.
        var annConversation = await CreateConversationAsync(ann, "my-planning-helper");
        var unconnected = await Host.SendAsync(ann, annConversation, "When is the next milestone of PRJ-3?");
        var card = unconnected.Single(evt => evt.Name == "connection.required").Data;
        Assert.Equal("personal-planning", card.GetProperty("serverId").GetString());
        Assert.Equal("not-connected", card.GetProperty("reason").GetString());
        var reloaded = await Host.GetJsonAsync(ann, $"/api/assistant/conversations/{annConversation}");
        Assert.Contains(
            reloaded.GetProperty("messages").EnumerateArray().Last().GetProperty("parts").EnumerateArray(),
            part => part.GetProperty("type").GetString() == "connection-required");

        // Ann connects from the card; the next answer uses her own account.
        authorization.Options.Subject = "remote-ann";
        await SampleMcpOAuthBrowser.ConnectAsync(ann, browser, "/api/assistant/mcp-connections/personal-planning/authorize");
        var connected = await Host.SendAsync(ann, annConversation, "When is the next milestone of PRJ-3?");
        Assert.Equal("succeeded", connected.Single(evt => evt.Name == "tool.completed").Data.GetProperty("status").GetString());
        Assert.Equal("remote-ann", planning.Resource!.Requests.Last().Subject);

        // Bob is asked for his own account; Ann's connection never serves him.
        var bobConversation = await CreateConversationAsync(bob, "my-planning-helper");
        var bobsTurn = await Host.SendAsync(bob, bobConversation, "When is the next milestone of PRJ-3?");
        Assert.Contains(bobsTurn, evt => evt.Name == "connection.required");
        Assert.DoesNotContain(bobsTurn, evt => evt.Name == "tool.started");

        // Ann disconnects in her preferences: the tokens are revoked.
        using var disconnected = await ann.DeleteAsync("/api/assistant/mcp-connections/personal-planning", cancellation);
        Assert.Equal(HttpStatusCode.NoContent, disconnected.StatusCode);
        Assert.Contains("refresh_token", authorization.Revocations);
        var listed = await Host.GetJsonAsync(ann, "/api/assistant/mcp-connections");
        Assert.Equal("not-connected", listed.EnumerateArray().Single(item => item.GetProperty("serverId").GetString() == "personal-planning")
            .GetProperty("connectionStatus").GetString());
    }

    private static async Task<bool> TestAsync(HttpClient admin, string serverId)
    {
        using var response = await admin.PostAsync($"/api/assistant/admin/mcp-servers/{serverId}/test", null, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await AssistantSampleHost.ReadJsonAsync(response)).GetProperty("ok").GetBoolean();
    }

    [Fact]
    public async Task SPM_252_an_edited_context_and_personal_preferences_shape_the_instructions()
    {
        var model = new NhAiScriptedChatClient().RespondWithText("Graag gedaan.");
        Host.Model.Use(model);
        using var admin = Host.CreateClient("spm-252-admin", admin: true);
        using var user = Host.CreateClient("spm-252-user");
        user.DefaultRequestHeaders.AcceptLanguage.Clear();
        user.DefaultRequestHeaders.AcceptLanguage.ParseAdd("nl-NL");

        var seeded = await Host.GetJsonAsync(admin, "/api/assistant/admin/context");
        var version = seeded.GetProperty("version").GetInt32();
        using var edited = await admin.PutAsync("/api/assistant/admin/context", AssistantSampleHost.Json(new
        {
            text = seeded.GetProperty("text").GetString() + "\nAlways mention the project key.",
            expectedVersion = version
        }), TestContext.Current.CancellationToken);
        using var preferences = await user.PutAsync("/api/assistant/preferences", AssistantSampleHost.Json(new
        {
            style = "direct",
            addressForm = "formal",
            responseLength = "short",
            customInstructions = "Answer in bullet points."
        }), TestContext.Current.CancellationToken);
        var conversationId = await Host.CreateConversationAsync(user);
        await Host.SendAsync(user, conversationId, "Which projects are running?");

        var instructions = model.Requests[0].Options!.Instructions!;
        Assert.Contains("Sample Project Management tracks projects per division.", seeded.GetProperty("text").GetString());
        Assert.Equal(HttpStatusCode.OK, edited.StatusCode);
        Assert.Equal(version + 1, (await AssistantSampleHost.ReadJsonAsync(edited)).GetProperty("version").GetInt32());
        Assert.Equal(HttpStatusCode.OK, preferences.StatusCode);
        Assert.Contains("Always mention the project key.", instructions);
        Assert.Contains("aan met u.", instructions); // Dutch formal address line, chosen from Accept-Language.
        Assert.Contains("Answer in bullet points.", instructions);
        Assert.True(instructions.IndexOf("# Application context", StringComparison.Ordinal)
            < instructions.IndexOf("# User preferences", StringComparison.Ordinal));
        Assert.Contains(
            Host.Services.GetRequiredService<SampleAssistantAuditLog>().Events,
            evt => evt.Kind == NhAssistantAuditEventKind.AdminContextUpdated);
    }

    private static async Task<Guid> CreateConversationAsync(HttpClient client, string agentId)
    {
        using var response = await client.PostAsync(
            "/api/assistant/conversations",
            AssistantSampleHost.Json(new { agentId }),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await AssistantSampleHost.ReadJsonAsync(response)).GetProperty("id").GetGuid();
    }
}

/// <summary>
/// SPM-299 and SPM-300: plays the person's browser in an MCP authorization. It opens the authorization
/// URL, follows the authorization server's redirect to the assistant's callback page, reads the
/// one-time completion code the page posts to its opener and completes the connection with the
/// client's own session.
/// </summary>
internal static class SampleMcpOAuthBrowser
{
    /// <summary>A browser without the person's session and without following redirects.</summary>
    public static HttpClient Create(HttpClient client)
    {
        return new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = client.BaseAddress };
    }

    public static async Task<JsonElement> ConnectAsync(HttpClient client, HttpClient browser, string authorizePath, object? request = null)
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var started = await client.PostAsync(authorizePath, request is null ? null : AssistantSampleHost.Json(request), cancellation);
        Assert.Equal(HttpStatusCode.OK, started.StatusCode);
        var start = await AssistantSampleHost.ReadJsonAsync(started);
        using var authorize = await browser.GetAsync(start.GetProperty("authorizationUrl").GetString(), cancellation);
        var redirect = authorize.Headers.Location!;
        Assert.StartsWith(AssistantSampleHost.OAuthRedirectUri, redirect.ToString(), StringComparison.Ordinal);
        using var callback = await browser.GetAsync("/api/assistant/mcp/oauth/callback" + redirect.Query, cancellation);
        var page = await callback.Content.ReadAsStringAsync(cancellation);
        var completionCode = System.Text.RegularExpressions.Regex.Match(page, "\"completionCode\":\"([^\"]+)\"").Groups[1].Value;
        using var completed = await client.PostAsync(
            "/api/assistant/mcp-connections/complete",
            AssistantSampleHost.Json(new { flowId = start.GetProperty("flowId").GetString(), completionCode }),
            cancellation);
        Assert.Equal(HttpStatusCode.OK, completed.StatusCode);
        return await AssistantSampleHost.ReadJsonAsync(completed);
    }
}

/// <summary>
/// A real Streamable HTTP MCP server built with the official SDK. It requires an API key, the
/// credential a test accepts or an OAuth access token, and records the API keys and
/// `Authorization` headers it receives.
/// </summary>
public sealed class SampleMcpServer : IAsyncDisposable
{
    private readonly WebApplication _app;

    private SampleMcpServer(
        WebApplication app,
        ConcurrentQueue<string> calls,
        ConcurrentQueue<string> apiKeys,
        ConcurrentQueue<string> authorizations)
    {
        _app = app;
        Calls = calls;
        ApiKeys = apiKeys;
        Authorizations = authorizations;
        Url = app.Urls.First() + "/mcp";
    }

    public string Url { get; }

    public ConcurrentQueue<string> Calls { get; }

    public ConcurrentQueue<string> ApiKeys { get; }

    public ConcurrentQueue<string> Authorizations { get; }

    public static Task<SampleMcpServer> StartAsync(string apiKey)
    {
        return StartAsync(request => string.Equals(request.Headers["X-Api-Key"].ToString(), apiKey, StringComparison.Ordinal));
    }

    /// <summary>
    /// The protected resource in front of a server started with <see cref="StartProtectedAsync"/>;
    /// null for other servers.
    /// </summary>
    public NhAiTestProtectedResource? Resource { get; private set; }

    /// <summary>
    /// SPM-299: a server that is a resource server of an OAuth authorization server. It publishes its
    /// protected resource metadata and requires an access token of <paramref name="authorization"/>
    /// for its own address, answering <c>401</c> with a challenge or <c>403 insufficient_scope</c>.
    /// </summary>
    public static async Task<SampleMcpServer> StartProtectedAsync(
        NhAiTestAuthorizationServer authorization,
        Action<NhAiTestProtectedResource>? configure = null)
    {
        SampleMcpServer? server = null;
        server = await StartAsync((context, next) => server!.Resource!.InvokeAsync(context, next));
        server.Resource = new NhAiTestProtectedResource(authorization, server.Url, "/mcp");
        configure?.Invoke(server.Resource);
        return server;
    }

    /// <summary>A server that answers only the requests <paramref name="accepts"/> allows.</summary>
    public static Task<SampleMcpServer> StartAsync(Func<HttpRequest, bool> accepts)
    {
        return StartAsync((context, next) =>
        {
            if (!accepts(context.Request))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return Task.CompletedTask;
            }
            return next();
        });
    }

    private static async Task<SampleMcpServer> StartAsync(Func<HttpContext, Func<Task>, Task> guard)
    {
        var calls = new ConcurrentQueue<string>();
        var apiKeys = new ConcurrentQueue<string>();
        var authorizations = new ConcurrentQueue<string>();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddMcpServer()
            .WithHttpTransport(options => options.Stateless = true)
            .WithTools(
            [
                McpServerTool.Create(
                    (string project) =>
                    {
                        calls.Enqueue(project);
                        return $"The next milestone of {project} is on Friday.";
                    },
                    new McpServerToolCreateOptions
                    {
                        Name = "next_milestone",
                        Description = "Returns the next milestone of a project.",
                        ReadOnly = true
                    })
            ]);
        var app = builder.Build();
        app.Use(async (context, next) =>
        {
            apiKeys.Enqueue(context.Request.Headers["X-Api-Key"].ToString());
            authorizations.Enqueue(context.Request.Headers.Authorization.ToString());
            await guard(context, () => next(context));
        });
        app.MapMcp("/mcp");
        await app.StartAsync();
        return new SampleMcpServer(app, calls, apiKeys, authorizations);
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
