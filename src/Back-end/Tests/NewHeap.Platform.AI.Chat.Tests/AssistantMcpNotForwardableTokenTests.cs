using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NewHeap.Platform.AI.AspNet;
using NewHeap.Platform.AI.Chat.AspNet.Mcp;
using NewHeap.Platform.AI.Chat.Entities;
using Xunit;

namespace NewHeap.Platform.AI.Chat.Tests;

/// <summary>
/// The public "never pass on" mark (FB58): a token that the application's own scheme marked with
/// <see cref="NhAiCallerCredentials.MarkNotForwardable"/> never reaches a <c>forward-user-token</c> MCP
/// server, as a token of the MCP exposure's protected resource never does, while an unmarked token of the
/// same request is forwarded to an allow-listed host as before.
/// </summary>
public sealed class AssistantMcpNotForwardableTokenTests
{
    private const string PersonToken = "person-token-of-the-request";

    [Fact]
    public async Task A_marked_token_never_reaches_a_forward_user_token_server_and_an_unmarked_one_does()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IHttpContextAccessor>(new HttpContextAccessor());
        services.AddSingleton<IAuthorizationService, AllowAllAuthorizationService>();
        services.AddNewHeapPlatformAIAspNet(_ => { });
        services.AddOptions<NhAssistantOptions>().Configure(options => options.Mcp.ForwardUserTokenHosts = ["planning.example.org"]);
        await using var provider = services.BuildServiceProvider();
        var server = new AssistantMcpServer
        {
            Id = "planning",
            Url = "https://planning.example.org/mcp",
            AuthMode = NhAssistantMcpAuthModes.ForwardUserToken
        };

        var unmarked = await ForwardAsync(provider, server, markNotForwardable: false);
        var marked = await ForwardAsync(provider, server, markNotForwardable: true);

        Assert.True(unmarked.Success);
        Assert.Equal("Bearer " + PersonToken, unmarked.Data!.Headers["Authorization"]);
        Assert.False(marked.Success);
        Assert.Equal(NhAssistantAdminErrorCodes.McpUnauthorized, marked.GetResultItems().Select(item => item.Name).First(name => !string.IsNullOrWhiteSpace(name)));
    }

    /// <summary>
    /// Asks the <c>forward-user-token</c> provider for the credential of a person's request whose bearer
    /// token the application's own scheme validated, and marked when <paramref name="markNotForwardable"/>.
    /// </summary>
    private static async Task<NewHeap.Platform.Common.Models.TaskResult<NhAssistantMcpCredential>> ForwardAsync(
        IServiceProvider services,
        AssistantMcpServer server,
        bool markNotForwardable)
    {
        await using var scope = services.CreateAsyncScope();
        var httpContext = new DefaultHttpContext
        {
            RequestServices = scope.ServiceProvider,
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "partner-app")], "partner-tokens"))
        };
        httpContext.Request.Headers.Authorization = "Bearer " + PersonToken;
        if (markNotForwardable)
        {
            NhAiCallerCredentials.MarkNotForwardable(httpContext, "https://partners.example.org");
        }

        var accessor = scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>();
        accessor.HttpContext = httpContext;
        try
        {
            var provider = new NhAssistantMcpForwardedTokenCredentialProvider(
                scope.ServiceProvider.GetRequiredService<IOptionsMonitor<NhAssistantOptions>>(),
                scope.ServiceProvider,
                NullLogger<NhAssistantMcpForwardedTokenCredentialProvider>.Instance);
            return await provider.GetAsync(server, NhAssistantMcpCredentialSubject.Request, CancellationToken.None);
        }
        finally
        {
            accessor.HttpContext = null;
        }
    }

    private sealed class AllowAllAuthorizationService : IAuthorizationService
    {
        public Task<AuthorizationResult> AuthorizeAsync(ClaimsPrincipal user, object? resource, IEnumerable<IAuthorizationRequirement> requirements)
        {
            return Task.FromResult(AuthorizationResult.Success());
        }

        public Task<AuthorizationResult> AuthorizeAsync(ClaimsPrincipal user, object? resource, string policyName)
        {
            return Task.FromResult(AuthorizationResult.Success());
        }
    }
}
