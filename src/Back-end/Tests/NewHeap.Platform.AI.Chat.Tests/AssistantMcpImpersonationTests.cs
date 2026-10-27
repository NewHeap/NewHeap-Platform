using System.Net;
using System.Security.Claims;
using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.AI.Chat.AspNet.Mcp;
using NewHeap.Platform.AI.Chat.Tests.Infrastructure;
using NewHeap.Platform.AI.Test;
using NewHeap.Platform.Common.Identity.Claims;
using Xunit;

namespace NewHeap.Platform.AI.Chat.Tests;

/// <summary>
/// An impersonation session acts as the person but grants nothing in the person's name that outlives
/// it: no consent to background use of an MCP server and no personal MCP connection, neither started
/// nor completed. Withdrawing a consent and disconnecting stay allowed, and their audit events name the
/// user who impersonated.
/// </summary>
public sealed partial class AssistantMcpOAuthTests
{
    private const string Impersonator = "admin-9";

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task An_impersonation_session_never_connects_the_persons_own_account_but_may_disconnect_it(
        AssistantTestProvider provider)
    {
        await using var authorization = await NhAiTestAuthorizationServer.StartAsync();
        authorization.RegisterClient("assistant-web", WebClientSecret, RedirectUri);
        await using var mcp = await ProtectedMcpServer.StartAsync(authorization);
        await using var host = await CreateHostAsync(provider);
        await AddPersonalServerAsync(host, mcp.Url, authorization);

        // Starting an authorization in an impersonation session is refused before anything is asked.
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var started = await scope.ServiceProvider.GetRequiredService<NhAssistantMcpConnectionService>()
                .AuthorizeAsync(AssistantTestHost.UserId, ImpersonatedUser(), "planning", null, true, CancellationToken.None);
            Assert.Equal(NhAssistantErrorCodes.NotAllowedWhileImpersonating, Code(started));
        }

        // An authorization the person started is not completed in an impersonation session: the flow
        // ends, and nobody can complete it afterwards.
        NhAssistantMcpAuthorizationStart start;
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var started = await scope.ServiceProvider.GetRequiredService<NhAssistantMcpConnectionService>()
                .AuthorizeAsync(AssistantTestHost.UserId, AssistantTestHost.EnterUser(scope.ServiceProvider, AssistantTestHost.UserId).User, "planning", null, false, CancellationToken.None);
            Assert.True(started.Success);
            start = started.Data!;
        }
        var outcome = await McpOAuthTestFlows.CallbackAsync(host, await McpOAuthTestFlows.AuthorizeAsync(start.AuthorizationUrl));
        var refused = await McpOAuthTestFlows.CompleteAsync(host, start.FlowId, outcome.CompletionCode!, AssistantTestHost.UserId, administrator: false, impersonating: true);
        Assert.Equal(NhAssistantErrorCodes.NotAllowedWhileImpersonating, Code(refused));
        Assert.Null((await FindPersonConnectionAsync(host, AssistantTestHost.UserId))?.Status);
        var afterwards = await McpOAuthTestFlows.CompleteAsync(host, start.FlowId, outcome.CompletionCode!, AssistantTestHost.UserId, administrator: false);
        Assert.Equal(NhAssistantAdminErrorCodes.McpAuthorizationExpired, Code(afterwards));

        // The person connects in their own session; the impersonation session may disconnect, and the
        // audit names who did.
        await McpOAuthTestFlows.ConnectPersonAsync(host, AssistantTestHost.UserId, "planning");
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var removed = await scope.ServiceProvider.GetRequiredService<NhAssistantMcpConnectionService>()
                .DisconnectAsync(AssistantTestHost.UserId, ImpersonatedUser(), "planning", CancellationToken.None);
            Assert.True(removed.Success);
            Assert.Equal(NhAssistantMcpPersonalServer.NotConnected, removed.Data!.ConnectionStatus);
        }
        Assert.NotEmpty(authorization.Revocations);
        var disconnected = Assert.Single(host.Business.Events, evt => evt.Kind == NhAssistantAuditEventKind.McpConnectionDisconnected
            && evt.ActorId == AssistantTestHost.UserId);
        Assert.Equal((NhAssistantMcpConnectionService.PersonReason, Impersonator), (disconnected.ResultCode, disconnected.ImpersonationOriginActorId));
        Assert.All(
            host.Business.Events.Where(evt => evt.Kind == NhAssistantAuditEventKind.McpConnectionConnected),
            evt => Assert.Null(evt.ImpersonationOriginActorId));
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task An_impersonation_session_withdraws_but_never_gives_consent_to_background_use(AssistantTestProvider provider)
    {
        await using var app = await AssistantWebApplication.StartAsync(
            database,
            new NhAiScriptedChatClient(),
            provider: provider,
            configure: services => services.AddSingleton<CapturedBusinessAuditSink>(),
            assistantBuilder: assistant => assistant
                .ConfigureMcp(options => options.ForwardUserTokenHosts = ["localhost"])
                .AddBusinessAuditSink<CapturedBusinessAuditSinkAdapter>());
        using var admin = app.CreateClient("admin-user", admin: true);
        using var member = app.CreateClient("member-1");
        using var impersonated = app.CreateClient("member-1", impersonatedBy: Impersonator);
        using var created = await admin.PostAsync("/api/assistant/admin/mcp-servers", Json(new
        {
            id = "planning",
            displayName = "Planning",
            url = "http://localhost/planning/mcp",
            authMode = "forward-user-token",
            isEnabled = true,
            headlessUse = "personal"
        }));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        await AssignToAgentAsync(app.Services);

        // The impersonation session neither consents nor starts a connection in the person's name.
        using (var consent = await impersonated.PutAsync("/api/assistant/mcp-connections/planning", Json(new { allowBackgroundUse = true })))
        {
            Assert.Equal(HttpStatusCode.Forbidden, consent.StatusCode);
            Assert.Equal(NhAssistantErrorCodes.NotAllowedWhileImpersonating, (await ReadJsonAsync(consent)).GetProperty("code").GetString());
        }
        using (var connect = await impersonated.PostAsync("/api/assistant/mcp-connections/planning/authorize", null))
        {
            Assert.Equal(HttpStatusCode.Forbidden, connect.StatusCode);
            Assert.Equal(NhAssistantErrorCodes.NotAllowedWhileImpersonating, (await ReadJsonAsync(connect)).GetProperty("code").GetString());
        }
        Assert.False(Assert.Single((await ReadJsonAsync(await member.GetAsync("/api/assistant/mcp-connections"))).EnumerateArray())
            .GetProperty("allowBackgroundUse").GetBoolean());

        // The person consents in their own session; the impersonation session may withdraw it.
        using (var own = await member.PutAsync("/api/assistant/mcp-connections/planning", Json(new { allowBackgroundUse = true })))
        {
            Assert.Equal(HttpStatusCode.OK, own.StatusCode);
        }
        using (var withdrawn = await impersonated.PutAsync("/api/assistant/mcp-connections/planning", Json(new { allowBackgroundUse = false })))
        {
            Assert.Equal(HttpStatusCode.OK, withdrawn.StatusCode);
            Assert.False((await ReadJsonAsync(withdrawn)).GetProperty("allowBackgroundUse").GetBoolean());
        }
        Assert.False(Assert.Single((await ReadJsonAsync(await member.GetAsync("/api/assistant/mcp-connections"))).EnumerateArray())
            .GetProperty("allowBackgroundUse").GetBoolean());

        var changes = app.Services.GetRequiredService<CapturedBusinessAuditSink>().Events
            .Where(evt => evt.Kind == NhAssistantAuditEventKind.McpBackgroundUseChanged)
            .Select(evt => (evt.ActorId, evt.ResultCode, evt.ImpersonationOriginActorId))
            .ToArray();
        Assert.Equal(
            [("member-1", NhAssistantMcpConnectionService.AllowedResult, (string?)null), ("member-1", NhAssistantMcpConnectionService.WithdrawnResult, Impersonator)],
            changes);
    }

    /// <summary>
    /// The person of the test host as an impersonation session of <see cref="Impersonator"/> authenticates
    /// them: the person's own claims and the NewHeap impersonation origin.
    /// </summary>
    private static ClaimsPrincipal ImpersonatedUser()
    {
        return new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, AssistantTestHost.UserId),
                new Claim(NhPlatformClaimTypes.ImpersonateOriginUserId, Impersonator)
            ],
            "test"));
    }
}
