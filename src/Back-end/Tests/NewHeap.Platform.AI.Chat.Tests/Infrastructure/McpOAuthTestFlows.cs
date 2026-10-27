using System.Net;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.AI.Chat.AspNet.Mcp;
using NewHeap.Platform.AI.Test;
using NewHeap.Platform.Common.Models;
using Xunit;

namespace NewHeap.Platform.AI.Chat.Tests.Infrastructure;

/// <summary>
/// Plays the browser's part of an MCP authorization against <see cref="NhAiTestAuthorizationServer"/>:
/// opens the authorization URL, hands its redirect to the assistant's callback and completes the flow
/// with the starter's session.
/// </summary>
internal static class McpOAuthTestFlows
{
    /// <summary>
    /// Connects a person's own account to a server each person connects to, as the person does from
    /// the chat or the preferences.
    /// </summary>
    public static async Task ConnectPersonAsync(
        AssistantTestHost host,
        string actorId,
        string serverId,
        bool allowBackgroundUse = false)
    {
        NhAssistantMcpAuthorizationStart start;
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var user = AssistantTestHost.EnterUser(scope.ServiceProvider, actorId).User;
            var started = await scope.ServiceProvider.GetRequiredService<NhAssistantMcpConnectionService>()
                .AuthorizeAsync(actorId, user, serverId, null, allowBackgroundUse, CancellationToken.None);
            Assert.True(started.Success);
            start = started.Data!;
        }
        var outcome = await CallbackAsync(host, await AuthorizeAsync(start.AuthorizationUrl));
        Assert.Null(outcome.FailureCode);
        Assert.True((await CompleteAsync(host, start.FlowId, outcome.CompletionCode!, actorId, administrator: false)).Success);
    }

    /// <summary>Opens the authorization URL and returns the redirect the authorization server answers.</summary>
    public static async Task<Uri> AuthorizeAsync(string authorizationUrl)
    {
        using var handler = new HttpClientHandler { AllowAutoRedirect = false };
        using var client = new HttpClient(handler);
        using var response = await client.GetAsync(authorizationUrl);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        return response.Headers.Location!;
    }

    public static async Task<NhAssistantMcpCallbackOutcome> CallbackAsync(AssistantTestHost host, Uri redirect)
    {
        var query = QueryHelpers.ParseQuery(redirect.Query);
        await using var scope = host.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<NhAssistantMcpAuthorizationFlows>().CallbackAsync(
            query.TryGetValue("state", out var state) ? state.ToString() : null,
            query.TryGetValue("code", out var code) ? code.ToString() : null,
            query.TryGetValue("iss", out var issuer) ? issuer.ToString() : null,
            query.TryGetValue("error", out var error) ? error.ToString() : null,
            CancellationToken.None);
    }

    public static async Task<TaskResult<NhAssistantMcpCompletion>> CompleteAsync(
        AssistantTestHost host,
        Guid flowId,
        string completionCode,
        string actorId,
        bool administrator,
        bool impersonating = false)
    {
        await using var scope = host.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<NhAssistantMcpAuthorizationFlows>().CompleteAsync(
            flowId,
            completionCode,
            actorId,
            () => Task.FromResult(administrator),
            impersonating,
            CancellationToken.None);
    }
}
