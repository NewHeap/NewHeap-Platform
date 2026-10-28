using System.Net;
using System.Net.Http.Headers;
using System.Text;
using ModelContextProtocol.Authentication;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using NewHeap.Platform.AI.Chat.Tests.Infrastructure;
using NewHeap.Platform.AI.Test;
using Xunit;

namespace NewHeap.Platform.AI.Chat.Tests;

/// <summary>
/// A NewHeap MCP exposure that is a protected resource (<c>UseProtectedResource</c>) over real Streamable
/// HTTP on loopback, with clients that follow the MCP authorization flow: the official MCP SDK client
/// discovers the authorization server from the challenge, registers, signs in with PKCE and a resource
/// indicator and lists the tools. A valid token of a person without rights still meets the tools' own
/// refusals, a token for the resource is never passed on, and the host's own sign-in keeps working.
/// </summary>
/// <remarks>
/// The tests live here rather than in NewHeap.Platform.AI.Tests because this project references the MCP
/// SDK's ASP.NET Core transport. The assistant's own client signs in to the same server in
/// <see cref="AssistantMcpProtectedResourceTests"/>.
/// </remarks>
public sealed class NhAiMcpProtectedResourceHttpTests(NhAiMcpProtectedResourceHttpTests.Host host)
    : IClassFixture<NhAiMcpProtectedResourceHttpTests.Host>
{
    private const string RedirectUri = "http://localhost:7071/callback";

    [Fact]
    public async Task An_mcp_request_without_a_token_or_with_a_token_for_another_resource_is_challenged_with_the_metadata_address()
    {
        using var client = new HttpClient();

        using var anonymous = await client.SendAsync(Initialize(host.Mcp.Url, token: null));
        using var foreign = await client.SendAsync(Initialize(
            host.Mcp.Url,
            host.Authorization.IssueAccessToken(ProtectedNewHeapMcpServer.Planner, host.Mcp.Origin + "/other", ProtectedNewHeapMcpServer.Scope)));

        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal(
            $"Bearer resource_metadata=\"{host.Mcp.MetadataAddress}\", scope=\"mcp:read\"",
            Assert.Single(anonymous.Headers.WwwAuthenticate).ToString());
        Assert.Equal(HttpStatusCode.Unauthorized, foreign.StatusCode);
        Assert.Equal(
            $"Bearer resource_metadata=\"{host.Mcp.MetadataAddress}\", scope=\"mcp:read\", error=\"invalid_token\"",
            Assert.Single(foreign.Headers.WwwAuthenticate).ToString());
    }

    [Fact]
    public async Task A_person_with_rights_lists_and_calls_the_tool_and_a_person_without_rights_meets_the_tools_own_refusals()
    {
        await using var planner = await ConnectAsync(host.Authorization.IssueAccessToken(
            ProtectedNewHeapMcpServer.Planner, host.Mcp.Url, ProtectedNewHeapMcpServer.Scope));
        await using var visitor = await ConnectAsync(host.Authorization.IssueAccessToken(
            ProtectedNewHeapMcpServer.Visitor, host.Mcp.Url, ProtectedNewHeapMcpServer.Scope));

        var plannerTools = await planner.ListToolsAsync();
        var plannerResult = await planner.CallToolAsync(
            ProtectedNewHeapMcpServer.ToolName,
            new Dictionary<string, object?> { ["input"] = "week" });
        var visitorTools = await visitor.ListToolsAsync();
        var visitorResult = await visitor.CallToolAsync(
            ProtectedNewHeapMcpServer.ToolName,
            new Dictionary<string, object?> { ["input"] = "week" });

        Assert.Equal([ProtectedNewHeapMcpServer.ToolName], plannerTools.Select(tool => tool.Name));
        Assert.NotEqual(true, plannerResult.IsError);
        // The token for the MCP resource is never available to pass on.
        Assert.Equal(
            ProtectedNewHeapMcpServer.Planner + "|" + ProtectedNewHeapMcpServer.NotPassedOn,
            plannerResult.StructuredContent!.Value.GetProperty("data").GetString());
        Assert.Empty(visitorTools);
        Assert.True(visitorResult.IsError);
        Assert.Equal("ai-tool-not-authorized", visitorResult.StructuredContent!.Value.GetProperty("code").GetString());
    }

    [Fact]
    public async Task The_host_sign_in_keeps_reaching_the_tools_and_its_own_token_as_before()
    {
        await using var client = await ConnectAsync(ProtectedNewHeapMcpServer.CreateHostToken(ProtectedNewHeapMcpServer.Planner));

        var result = await client.CallToolAsync(
            ProtectedNewHeapMcpServer.ToolName,
            new Dictionary<string, object?> { ["input"] = "week" });

        Assert.NotEqual(true, result.IsError);
        Assert.Equal(
            ProtectedNewHeapMcpServer.Planner + "|" + ProtectedNewHeapMcpServer.PassedOn,
            result.StructuredContent!.Value.GetProperty("data").GetString());
    }

    [Fact]
    public async Task The_official_sdk_client_follows_the_challenge_registers_signs_in_with_the_resource_and_lists_the_tools()
    {
        host.Authorization.Options.Subject = ProtectedNewHeapMcpServer.Planner;
        var tokenRequests = host.Authorization.TokenRequests.Count;
        using var browser = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });

        await using var client = await McpClient.CreateAsync(new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(host.Mcp.Url),
            TransportMode = HttpTransportMode.StreamableHttp,
            OAuth = new ClientOAuthOptions
            {
                RedirectUri = new Uri(RedirectUri),
                DynamicClientRegistration = new DynamicClientRegistrationOptions { ClientName = "Any MCP client" },
                // The person approves at the authorization server; the redirect carries the code.
                AuthorizationRedirectDelegate = async (authorizationUri, _, cancellationToken) =>
                {
                    using var response = await browser.GetAsync(authorizationUri, cancellationToken);
                    Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
                    var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(response.Headers.Location!.Query);
                    return query["code"].ToString();
                }
            }
        }));
        var tools = await client.ListToolsAsync();

        Assert.Equal([ProtectedNewHeapMcpServer.ToolName], tools.Select(tool => tool.Name));
        Assert.NotEmpty(host.Authorization.DynamicClients);
        var tokenRequest = host.Authorization.TokenRequests.Skip(tokenRequests).First(request => request.GrantType == "authorization_code");
        Assert.Equal(host.Mcp.Url, tokenRequest.Resource);
    }

    private async Task<McpClient> ConnectAsync(string token)
    {
        return await McpClient.CreateAsync(new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(host.Mcp.Url),
            TransportMode = HttpTransportMode.StreamableHttp,
            AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = "Bearer " + token }
        }));
    }

    private static HttpRequestMessage Initialize(string url, string? token)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(
                """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"probe","version":"1.0"}}}""",
                Encoding.UTF8,
                "application/json")
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return request;
    }

    /// <summary>
    /// The authorization server and the protected NewHeap MCP exposure, started once for the class.
    /// </summary>
    public sealed class Host : IAsyncLifetime
    {
        private NhAiTestAuthorizationServer? _authorization;
        private ProtectedNewHeapMcpServer? _mcp;

        public NhAiTestAuthorizationServer Authorization => _authorization
            ?? throw new InvalidOperationException("The authorization server did not start.");

        internal ProtectedNewHeapMcpServer Mcp => _mcp
            ?? throw new InvalidOperationException("The MCP server did not start.");

        public async Task InitializeAsync()
        {
            _authorization = await NhAiTestAuthorizationServer.StartAsync(options => options.ScopesSupported = [ProtectedNewHeapMcpServer.Scope]);
            _mcp = await ProtectedNewHeapMcpServer.StartAsync(_authorization);
        }

        public async Task DisposeAsync()
        {
            if (_mcp is not null)
            {
                await _mcp.DisposeAsync();
            }
            if (_authorization is not null)
            {
                await _authorization.DisposeAsync();
            }
        }
    }
}
