using System.Net;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.AI.Chat.AspNet.Mcp;
using NewHeap.Platform.AI.Chat.Tests.Infrastructure;
using NewHeap.Platform.AI.Test;
using NewHeap.Platform.Common.Models;
using Xunit;

namespace NewHeap.Platform.AI.Chat.Tests;

/// <summary>
/// The round trip of FB42: a NewHeap host's own MCP client, the assistant, discovers a NewHeap MCP
/// exposure that is a protected resource (<c>UseProtectedResource</c>) from its challenge and metadata,
/// registers itself at the authorization server, signs the organization's account in with PKCE and a
/// resource indicator, and reaches the tools that account may use. The client knows nothing of the
/// server beyond the MCP authorization flow, as any other client.
/// </summary>
public sealed class AssistantMcpProtectedResourceTests(AssistantDatabaseFixture database) : IClassFixture<AssistantDatabaseFixture>
{
    private const string RedirectUri = "https://assistant.example/api/assistant/mcp/oauth/callback";
    private const string ServerId = "planning";

    [Fact]
    public async Task The_assistant_discovers_a_newheap_mcp_exposure_signs_in_through_its_authorization_server_and_reaches_only_the_accounts_tools()
    {
        await using var authorization = await NhAiTestAuthorizationServer.StartAsync(options =>
        {
            options.ScopesSupported = [ProtectedNewHeapMcpServer.Scope];
            options.Subject = ProtectedNewHeapMcpServer.Planner;
        });
        await using var mcp = await ProtectedNewHeapMcpServer.StartAsync(authorization);
        await using var host = await AssistantTestHost.CreateAsync(
            database,
            AssistantTestProvider.PostgreSql,
            new NhAiScriptedChatClient(),
            assistantBuilder: assistant => assistant.ConfigureMcp(options =>
            {
                options.RequireHttps = true;
                options.OAuth.RedirectUri = RedirectUri;
            }));

        // Discovery reads the protected resource metadata of the exposure and its authorization server.
        // The assistant probes with GET, which a stateless exposure does not map (405), so it finds the
        // metadata at the well-known address of the resource, as RFC 9728 lets every client.
        var discovered = await AdministerAsync(host, administration => administration.DiscoverAsync(mcp.Url, CancellationToken.None));
        Assert.True(discovered.Success);
        Assert.Equal(mcp.Url, discovered.Data!.Resource);
        Assert.Equal([authorization.Issuer], discovered.Data.AuthorizationServers);
        Assert.Equal(authorization.Issuer, discovered.Data.AuthorizationServer.Issuer);
        Assert.Equal([ProtectedNewHeapMcpServer.Scope], discovered.Data.ResourceScopes);

        // The administrator confirms the authorization server; the assistant registers itself there.
        var created = await AdministerAsync(host, administration => administration.CreateAsync(
            new NhAssistantMcpServerInput(ServerId, "Planning", mcp.Url, NhAssistantMcpAuthModes.OAuth, null, null, null, true)
            {
                ConnectionScope = NhAssistantMcpConnectionScopes.Organization,
                OAuth = new NhAssistantMcpOAuthInput(null, null, null, ProtectedNewHeapMcpServer.Scope, "Planning account", ConfirmAuthorization: true)
            },
            "admin-1",
            CancellationToken.None));
        Assert.True(created.Success);

        // The planner signs the organization's account in and the assistant reaches the planner's tool.
        await ConnectOrganizationAsync(host);
        Assert.Single(authorization.DynamicClients);
        var tokenRequest = authorization.TokenRequests.Single(request => request.GrantType == "authorization_code");
        Assert.Equal(mcp.Url, tokenRequest.Resource);
        var planner = await AdministerAsync(host, administration => administration.TestAsync(ServerId, CancellationToken.None));
        Assert.True(planner.Data!.Ok, planner.Data.Code);
        Assert.Equal(1, planner.Data.ToolCount);
        var synchronized = await AdministerAsync(host, administration => administration.SyncAsync(ServerId, "admin-1", CancellationToken.None));
        Assert.Equal([ProtectedNewHeapMcpServer.ToolName], synchronized.Data!.Select(tool => tool.RemoteName));

        // Signed in as a person without rights, the sign-in holds and the tools' own refusals decide.
        Assert.True((await AdministerAsync(host, administration => administration.DisconnectOrganizationAsync(ServerId, "admin-1", CancellationToken.None))).Success);
        authorization.Options.Subject = ProtectedNewHeapMcpServer.Visitor;
        await ConnectOrganizationAsync(host);
        var visitor = await AdministerAsync(host, administration => administration.TestAsync(ServerId, CancellationToken.None));
        Assert.True(visitor.Data!.Ok, visitor.Data.Code);
        Assert.Equal(0, visitor.Data.ToolCount);
    }

    private static async Task<T> AdministerAsync<T>(AssistantTestHost host, Func<NhAssistantMcpAdministration, Task<T>> action)
    {
        await using var scope = host.Services.CreateAsyncScope();
        AssistantTestHost.EnterUser(scope.ServiceProvider, "admin-1");
        return await action(scope.ServiceProvider.GetRequiredService<NhAssistantMcpAdministration>());
    }

    /// <summary>Runs the organization authorization the way an administrator's browser does.</summary>
    private static async Task ConnectOrganizationAsync(AssistantTestHost host)
    {
        var started = await AdministerAsync(host, administration => administration.AuthorizeOrganizationAsync(ServerId, "admin-1", null, CancellationToken.None));
        Assert.True(started.Success);
        var start = started.Data!;

        Uri redirect;
        using (var handler = new HttpClientHandler { AllowAutoRedirect = false })
        using (var browser = new HttpClient(handler))
        using (var response = await browser.GetAsync(start.AuthorizationUrl))
        {
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            redirect = response.Headers.Location!;
        }

        var query = QueryHelpers.ParseQuery(redirect.Query);
        NhAssistantMcpCallbackOutcome outcome;
        await using (var scope = host.Services.CreateAsyncScope())
        {
            outcome = await scope.ServiceProvider.GetRequiredService<NhAssistantMcpAuthorizationFlows>().CallbackAsync(
                query.TryGetValue("state", out var state) ? state.ToString() : null,
                query.TryGetValue("code", out var code) ? code.ToString() : null,
                query.TryGetValue("iss", out var issuer) ? issuer.ToString() : null,
                query.TryGetValue("error", out var error) ? error.ToString() : null,
                CancellationToken.None);
        }
        Assert.Null(outcome.FailureCode);

        TaskResult<NhAssistantMcpCompletion> completed;
        await using (var scope = host.Services.CreateAsyncScope())
        {
            completed = await scope.ServiceProvider.GetRequiredService<NhAssistantMcpAuthorizationFlows>().CompleteAsync(
                start.FlowId,
                outcome.CompletionCode!,
                "admin-1",
                () => Task.FromResult(true),
                impersonating: false,
                CancellationToken.None);
        }
        Assert.True(completed.Success);
    }
}
