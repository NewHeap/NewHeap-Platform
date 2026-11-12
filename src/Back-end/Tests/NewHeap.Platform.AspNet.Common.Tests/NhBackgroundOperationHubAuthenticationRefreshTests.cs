using System.Net;
using System.Net.WebSockets;
using System.Security.Claims;
using System.Text.Json;
using AwesomeAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using NewHeap.Platform.AspNet.Common.Authentication;
using NewHeap.Platform.AspNet.Common.Services.BackgroundOperations;
using NewHeap.Platform.Common.Identity.Claims;
using Xunit;
using static NewHeap.Platform.AspNet.Common.Tests.BackgroundOperationHubTestHost;

namespace NewHeap.Platform.AspNet.Common.Tests;

/// <summary>
/// SignalR authentication refresh on the background operation hub
/// (<see cref="NhBackgroundOperationsOptions.HubAuthenticationRefresh"/>). On ASP.NET Core 11 with the
/// opt-in, a browser-like WebSocket connection presents a renewed token at <c>/refresh</c>: for the same
/// person it outlives the token it connected with and keeps receiving its own and its division's
/// changes; a token of another person is refused with 403 and the connection stays with its person until
/// its own token expires; an expired token and a removed user are refused with 401; and a person who lost
/// the connection's active division is disconnected after the refresh. Without the opt-in, and on
/// ASP.NET Core 10, the hub maps no refresh endpoint. The tests need no relational database: no operation
/// is stored, and the host's startup check reads an in-memory context.
/// </summary>
public sealed class NhBackgroundOperationHubAuthenticationRefreshTests
{
    [Fact]
    public async Task Without_the_opt_in_the_hub_maps_no_refresh_endpoint()
    {
        await using var app = await StartAsync(authenticationRefresh: false, new HubTestDivisions(), new HubTestSessions());
        var userId = Guid.NewGuid();
        using var connection = await ConnectAsync(app.GetTestServer(), CreateToken(userId));

        using var refreshed = await RefreshAsync(app.GetTestServer(), connection.ConnectionToken, CreateToken(userId));

        refreshed.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

#if !NET11_0_OR_GREATER
    [Fact]
    public async Task On_ASP_NET_Core_10_the_opt_in_maps_no_refresh_endpoint()
    {
        await using var app = await StartAsync(authenticationRefresh: true, new HubTestDivisions(), new HubTestSessions());
        var userId = Guid.NewGuid();
        using var connection = await ConnectAsync(app.GetTestServer(), CreateToken(userId));

        using var refreshed = await RefreshAsync(app.GetTestServer(), connection.ConnectionToken, CreateToken(userId));

        refreshed.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
#endif

    [Fact]
    public void Only_the_same_person_with_the_same_impersonation_origin_is_the_same_person()
    {
        var alice = Guid.NewGuid().ToString();
        var admin = Guid.NewGuid().ToString();

        NhHubAuthenticationRefresh.IsSamePerson(Principal(alice), Principal(alice)).Should().BeTrue();
        NhHubAuthenticationRefresh.IsSamePerson(Principal(alice), Principal(Guid.NewGuid().ToString())).Should().BeFalse();
        NhHubAuthenticationRefresh.IsSamePerson(Principal(alice), Principal(alice, admin)).Should().BeFalse();
        NhHubAuthenticationRefresh.IsSamePerson(Principal(alice, admin), Principal(alice)).Should().BeFalse();
        NhHubAuthenticationRefresh.IsSamePerson(Principal(alice, admin), Principal(alice, admin)).Should().BeTrue();
        NhHubAuthenticationRefresh.IsSamePerson(Principal(alice), new ClaimsPrincipal(new ClaimsIdentity())).Should().BeFalse();
    }

#if NET11_0_OR_GREATER
    [Fact]
    public async Task A_refresh_for_the_same_person_keeps_the_connection_past_its_first_token_and_it_still_receives()
    {
        var divisions = new HubTestDivisions();
        await using var app = await StartAsync(authenticationRefresh: true, divisions, new HubTestSessions());
        var server = app.GetTestServer();
        var userId = Guid.NewGuid();
        var divisionId = Guid.NewGuid();
        divisions.Grant(userId, divisionId);

        // The refreshing connection's first token expires before the witness's; once the witness closed,
        // the hub has checked the refreshing connection after its first token expired.
        using var refreshing = await ConnectAsync(server, CreateToken(userId, TimeSpan.FromSeconds(2)), divisionId);
        using var witness = await ConnectAsync(server, CreateToken(Guid.NewGuid(), TimeSpan.FromSeconds(4)));

        using var refreshed = await RefreshAsync(server, refreshing.ConnectionToken, CreateToken(userId, TimeSpan.FromMinutes(10)));

        refreshed.StatusCode.Should().Be(HttpStatusCode.OK);
        using (var body = JsonDocument.Parse(await refreshed.Content.ReadAsStringAsync()))
        {
            body.RootElement.GetProperty("tokenLifetimeSeconds").GetInt64().Should().BeGreaterThan(60);
        }

        await witness.Connection.WaitForCloseAsync();

        var global = await PublishAsync(app, userId, null);
        var inDivision = await PublishAsync(app, userId, divisionId);
        (await NextOperationIdAsync(refreshing)).Should().Be(global);
        (await NextOperationIdAsync(refreshing)).Should().Be(inDivision);
        refreshing.Socket.State.Should().Be(WebSocketState.Open);
    }

    [Fact]
    public async Task A_refresh_with_another_persons_token_is_refused_and_the_connection_stays_with_its_person_until_its_token_expires()
    {
        await using var app = await StartAsync(authenticationRefresh: true, new HubTestDivisions(), new HubTestSessions());
        var server = app.GetTestServer();
        var alice = Guid.NewGuid();
        var bob = Guid.NewGuid();
        using var connection = await ConnectAsync(server, CreateToken(alice, TimeSpan.FromSeconds(3)));

        // An account switch in the browser: the connection may not become Bob's.
        using var refreshed = await RefreshAsync(server, connection.ConnectionToken, CreateToken(bob, TimeSpan.FromMinutes(10)));

        refreshed.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        await PublishAsync(app, bob, null);
        var aliceOperation = await PublishAsync(app, alice, null);
        (await NextOperationIdAsync(connection)).Should().Be(aliceOperation);
        await connection.Connection.WaitForCloseAsync();
    }

    [Fact]
    public async Task A_refresh_with_an_expired_token_or_for_a_removed_user_is_refused_and_the_connection_still_closes_when_its_token_expires()
    {
        var sessions = new HubTestSessions();
        await using var app = await StartAsync(authenticationRefresh: true, new HubTestDivisions(), sessions);
        var server = app.GetTestServer();
        var alice = Guid.NewGuid();
        using var connection = await ConnectAsync(server, CreateToken(alice, TimeSpan.FromSeconds(3)));

        using var expired = await RefreshAsync(server, connection.ConnectionToken, CreateExpiredToken(alice));
        sessions.Remove(alice);
        using var afterRemoval = await RefreshAsync(server, connection.ConnectionToken, CreateToken(alice, TimeSpan.FromMinutes(10)));

        expired.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        afterRemoval.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        await connection.Connection.WaitForCloseAsync();
    }

    [Fact]
    public async Task After_a_refresh_the_hub_closes_the_connection_of_a_person_who_lost_its_active_division()
    {
        var divisions = new HubTestDivisions();
        await using var app = await StartAsync(authenticationRefresh: true, divisions, new HubTestSessions());
        var server = app.GetTestServer();
        var alice = Guid.NewGuid();
        var divisionId = Guid.NewGuid();
        divisions.Grant(alice, divisionId);
        using var connection = await ConnectAsync(server, CreateToken(alice), divisionId);

        divisions.Revoke(alice, divisionId);
        using var refreshed = await RefreshAsync(server, connection.ConnectionToken, CreateToken(alice, TimeSpan.FromMinutes(20)));

        // The refresh itself is the same person's; the hub then checks the division again.
        refreshed.StatusCode.Should().Be(HttpStatusCode.OK);
        await connection.Connection.WaitForCloseAsync();
    }

    [Fact]
    public async Task A_refresh_right_after_the_handshake_keeps_the_connection_and_its_groups()
    {
        var divisions = new HubTestDivisions();
        await using var app = await StartAsync(authenticationRefresh: true, divisions, new HubTestSessions());
        var server = app.GetTestServer();
        var alice = Guid.NewGuid();
        var divisionId = Guid.NewGuid();
        divisions.Grant(alice, divisionId);

        // A client may refresh while the hub still joins its groups; the hub then checks the refreshed
        // principal once it joined them.
        using var connection = await ConnectAsync(server, CreateToken(alice), divisionId, waitUntilJoined: false);
        using var refreshed = await RefreshAsync(server, connection.ConnectionToken, CreateToken(alice, TimeSpan.FromMinutes(20)));
        await connection.Connection.SendAsync("""{"type":1,"invocationId":"joined","target":"NotAHubMethod","arguments":[]}""");
        (await connection.Connection.ReceiveMessageAsync(type: 3)).GetProperty("invocationId").GetString().Should().Be("joined");

        refreshed.StatusCode.Should().Be(HttpStatusCode.OK);
        var inDivision = await PublishAsync(app, alice, divisionId);
        (await NextOperationIdAsync(connection)).Should().Be(inDivision);
    }
#endif

    private static Task<WebApplication> StartAsync(bool authenticationRefresh, HubTestDivisions divisions, HubTestSessions sessions)
    {
        return BackgroundOperationHubTestHost.StartAsync(
            sessions,
            divisions,
            options => options.HubAuthenticationRefresh = authenticationRefresh);
    }

    private static ClaimsPrincipal Principal(string userId, string? impersonationOrigin = null)
    {
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, userId) };
        if (impersonationOrigin is not null)
        {
            claims.Add(new Claim(NhPlatformClaimTypes.ImpersonateOriginUserId, impersonationOrigin));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
    }
}
