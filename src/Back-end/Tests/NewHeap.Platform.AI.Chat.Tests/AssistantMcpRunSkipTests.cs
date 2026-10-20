using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.AI.Chat.AspNet.Mcp;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.AI.Chat.Runs;
using NewHeap.Platform.AI.Chat.Runtime;
using NewHeap.Platform.AI.Chat.Tests.Infrastructure;
using NewHeap.Platform.AI.Test;
using Xunit;

namespace NewHeap.Platform.AI.Chat.Tests;

/// <summary>
/// A personal MCP server a headless run left out (follow-up F3): the run records the server and the
/// skip code, and the person whose consent or connection is missing is told through the agent
/// notifications, at most once per server and day.
/// </summary>
public sealed partial class AssistantMcpHeadlessTests
{
    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task A_run_records_a_personal_server_it_left_out_and_tells_the_person_once_a_day(AssistantTestProvider provider)
    {
        const string redirectUri = "https://assistant.example/api/assistant/mcp/oauth/callback";
        var model = new NhAiScriptedChatClient()
            .RespondWithText("I cannot read the planning.")
            .RespondWithText("Still no planning.")
            .RespondWithText("Connect the planning first.");
        await using var authorization = await NhAiTestAuthorizationServer.StartAsync();
        authorization.RegisterClient("assistant-web", "test-web-secret-skips", redirectUri);
        var tools = new InMemoryMcpServers();
        NhAiTestProtectedResource? resource = null;
        await using var mcp = await HttpMcpTestServer.StartAsync(() => tools.PlanningTools(), guard: (context, next) => resource!.InvokeAsync(context, next));
        resource = new NhAiTestProtectedResource(authorization, mcp.StreamableUrl, "/planning");
        await using var host = await CreateHostAsync(provider, model, servers: null, delegation: true, grants: false, redirectUri: redirectUri);
        var input = new NhAssistantMcpServerInput("planning", "Planning", mcp.StreamableUrl, NhAssistantMcpAuthModes.OAuth, null, null, null, true, NhAssistantMcpHeadlessUses.Personal)
        {
            ConnectionScope = NhAssistantMcpConnectionScopes.User,
            OAuth = new NhAssistantMcpOAuthInput("assistant-web", "test-web-secret-skips", null, "mcp:read", null, ConfirmAuthorization: true)
        };
        Assert.True((await WithAdministrationAsync(host, administration => administration.CreateAsync(input, "admin-1", CancellationToken.None))).Success);
        authorization.Options.Subject = "remote-admin";
        await AuthorizeAdministratorAsync(host);
        await ConnectPlanningServerAsync(host, ("Lookup_Order", NhAssistantMcpToolEffects.ReadOnly), input: input, created: true);
        authorization.Options.Subject = "remote-member";
        await McpOAuthTestFlows.ConnectPersonAsync(host, Member, "planning");
        var channel = host.Services.GetRequiredService<RecordingChannel>();

        // Connected, but without consent to background use: the run leaves the server out, records
        // why and tells the member.
        var first = await StartRunAsync(host, initiator: Member);
        var withoutConsent = await RunTurnAsync(host, first, "Project PRJ-1 was created.");
        var missing = Assert.Single(withoutConsent.MissingMcpConnections);
        Assert.Equal(("planning", "Planning", NhAssistantAdminErrorCodes.McpConsentRequired, Member), (missing.ServerId, missing.DisplayName, missing.Code, missing.PersonActorId));
        await RecordAsync(host, first, withoutConsent);

        Assert.Equal(
            new[] { new NhAssistantRunMcpSkip("planning", "Planning", NhAssistantAdminErrorCodes.McpConsentRequired) },
            await SkipsOfAsync(host, first));
        var told = Assert.Single(channel.Deliveries);
        Assert.Equal(NhAssistantAgentNotificationKind.McpConnectionMissing, told.Context.Kind);
        Assert.Equal(new[] { Member }, told.Recipients);
        Assert.Equal(("planning", "Planning", Member, NhAssistantAdminErrorCodes.McpConsentRequired),
            (told.Context.McpServerId, told.Context.McpServerDisplayName, told.Context.McpConnectionPersonId, told.Context.FailureCode));
        Assert.Equal(first.Run.Id, told.Context.RunId);

        // Another run the same day records it too; the member is not told again.
        var second = await StartRunAsync(host, initiator: Member);
        var again = await RunTurnAsync(host, second, "Project PRJ-1 changed.");
        await RecordAsync(host, second, again);
        Assert.Single(await SkipsOfAsync(host, second));
        Assert.Single(channel.Deliveries);

        // The member consents and disconnects: the consent stays, the connection is missing. A day
        // after the last notice the member is told again, with the new reason.
        await McpOAuthTestFlows.ConnectPersonAsync(host, Member, "planning", allowBackgroundUse: true);
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var user = AssistantTestHost.EnterUser(scope.ServiceProvider, Member).User;
            Assert.True((await scope.ServiceProvider.GetRequiredService<NhAssistantMcpConnectionService>()
                .DisconnectAsync(Member, user, "planning", CancellationToken.None)).Success);
        }
        await using (var context = host.Services.GetRequiredService<NhAssistantDbContextFactory>().CreateDbContext())
        {
            await context.McpConnectionNotices.ExecuteUpdateAsync(setters => setters.SetProperty(
                notice => notice.NotifiedAt,
                DateTimeOffset.UtcNow - NhAssistantMcpConnectionNotices.NoticeInterval - TimeSpan.FromMinutes(1)));
        }
        var third = await StartRunAsync(host, initiator: Member);
        var notConnected = await RunTurnAsync(host, third, "Project PRJ-1 changed again.");
        Assert.Equal(NhAssistantAdminErrorCodes.McpConnectionRequired, Assert.Single(notConnected.MissingMcpConnections).Code);
        await RecordAsync(host, third, notConnected);
        Assert.Equal(2, channel.Deliveries.Count);
        Assert.Equal(NhAssistantAdminErrorCodes.McpConnectionRequired, channel.Deliveries.Last().Context.FailureCode);

        // A turn that records more servers keeps one entry per server with its latest code.
        await RecordAsync(host, third, notConnected with
        {
            MissingMcpConnections = [new NhAssistantMcpMissingConnection("planning", "Planning", NhAssistantAdminErrorCodes.McpReauthorizationRequired, Member)]
        });
        Assert.Equal(
            new[] { new NhAssistantRunMcpSkip("planning", "Planning", NhAssistantAdminErrorCodes.McpReauthorizationRequired) },
            await SkipsOfAsync(host, third));

        // The maintenance removes notices older than their retention.
        await using (var context = host.Services.GetRequiredService<NhAssistantDbContextFactory>().CreateDbContext())
        {
            await context.McpConnectionNotices.ExecuteUpdateAsync(setters => setters.SetProperty(
                notice => notice.NotifiedAt,
                DateTimeOffset.UtcNow - NhAssistantMcpConnectionNotices.NoticeRetention - TimeSpan.FromMinutes(1)));
        }
        await new NhAssistantMcpMaintenance(
                host.Services.GetRequiredService<IServiceScopeFactory>(),
                host.Services.GetRequiredService<NhAssistantRegistrationState>(),
                Microsoft.Extensions.Logging.Abstractions.NullLogger<NhAssistantMcpMaintenance>.Instance)
            .RunOnceAsync(CancellationToken.None);
        await using (var context = host.Services.GetRequiredService<NhAssistantDbContextFactory>().CreateDbContext())
        {
            Assert.Empty(await context.McpConnectionNotices.ToListAsync());
        }
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task Concurrent_notices_for_a_person_and_server_are_claimed_once(AssistantTestProvider provider)
    {
        await using var host = await CreateHostAsync(provider, new NhAiScriptedChatClient(), servers: null);
        Assert.True((await WithAdministrationAsync(host, administration => administration.CreateAsync(
            Server(NhAssistantMcpAuthModes.None), "admin-1", CancellationToken.None))).Success);
        await using var scope = host.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<NhAssistantNotificationStore>();
        var now = DateTimeOffset.UtcNow;

        var claims = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => store.TryClaimMcpConnectionNoticeAsync(
            "planning", Member, NhAssistantAdminErrorCodes.McpConsentRequired, now, now - NhAssistantMcpConnectionNotices.NoticeInterval, CancellationToken.None)));

        Assert.Single(claims, claimed => claimed);
        // Another person and a later day each claim their own.
        Assert.True(await store.TryClaimMcpConnectionNoticeAsync("planning", Manager, NhAssistantAdminErrorCodes.McpConsentRequired, now, now - NhAssistantMcpConnectionNotices.NoticeInterval, CancellationToken.None));
        var tomorrow = now + NhAssistantMcpConnectionNotices.NoticeInterval + TimeSpan.FromMinutes(1);
        Assert.True(await store.TryClaimMcpConnectionNoticeAsync("planning", Member, NhAssistantAdminErrorCodes.McpConnectionRequired, tomorrow, tomorrow - NhAssistantMcpConnectionNotices.NoticeInterval, CancellationToken.None));
    }

    private static async Task RecordAsync(AssistantTestHost host, StartedRun run, NhAssistantAgentTurnResult result)
    {
        await using var scope = host.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<NhAssistantMcpConnectionNotices>()
            .RecordAsync(run.Run.Id, run.AgentTurn.Thread, result.MissingMcpConnections, CancellationToken.None);
    }

    private static async Task<IReadOnlyList<NhAssistantRunMcpSkip>> SkipsOfAsync(AssistantTestHost host, StartedRun run)
    {
        var stored = await RunStore(host).FindRunAsync(run.Run.Id, CancellationToken.None);
        return NhAssistantRunMcpSkips.Read(stored!.McpConnectionSkips);
    }
}
