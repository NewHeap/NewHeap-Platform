using System.Net;
using System.Net.WebSockets;
using AwesomeAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using NewHeap.Platform.AspNet.Common.Services.BackgroundOperations;
using Xunit;
using static NewHeap.Platform.AspNet.Common.Tests.BackgroundOperationHubTestHost;

namespace NewHeap.Platform.AspNet.Common.Tests;

/// <summary>
/// The background operation hub's session check
/// (<see cref="NhBackgroundOperationsOptions.HubSessionCheckEnabled"/>, on by default). Every
/// <see cref="NhBackgroundOperationsOptions.HubSessionCheckInterval"/> on the application's clock the hub
/// validates each open connection's principal again with the session validator: a removed user's
/// connection and one whose sessions were revoked close at the next check, while a valid user's
/// connection stays and keeps receiving its operations' changes. Without the check nothing watches a
/// connection. On ASP.NET Core 11 with <see cref="NhBackgroundOperationsOptions.HubAuthenticationRefresh"/>
/// the check validates the principal of the last accepted refresh. The tests move the clock instead of
/// waiting for the interval, and need no relational database.
/// </summary>
public sealed class NhBackgroundOperationHubSessionTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan Interval = new NhBackgroundOperationsOptions().HubSessionCheckInterval;

    [Fact]
    public async Task A_removed_user_and_one_whose_sessions_were_revoked_are_disconnected_at_the_next_check_while_a_valid_user_stays()
    {
        var sessions = new HubTestSessions();
        var clock = new HubTestClock();
        await using var app = await StartAsync(new HubTestDivisions(), sessions, clock);
        var server = app.GetTestServer();
        var removed = Guid.NewGuid();
        var revoked = Guid.NewGuid();
        var valid = Guid.NewGuid();
        var watched = clock.TimersCreated;
        using var removedConnection = await ConnectAsync(server, CreateToken(removed));
        using var revokedConnection = await ConnectAsync(server, CreateToken(revoked));
        using var validConnection = await ConnectAsync(server, CreateToken(valid));
        clock.TimersCreated.Should().Be(watched + 3, "every connection's session is watched once it joined its groups");

        // One user is deleted; the other changed their password, which changed their security stamp, so
        // the token they connected with no longer counts. Both tokens stay valid for ten minutes.
        sessions.Remove(removed);
        sessions.Accept(revoked, "s2");
        var validChecked = sessions.NextValidation(valid);
        clock.Advance(Interval);

        await removedConnection.Connection.WaitForCloseAsync();
        await revokedConnection.Connection.WaitForCloseAsync();
        (await validChecked.WaitAsync(Patience)).Should().Be(HubTestSessions.FirstStamp);
        var operation = await PublishAsync(app, valid, null);
        (await NextOperationIdAsync(validConnection)).Should().Be(operation);
        validConnection.Socket.State.Should().Be(WebSocketState.Open);
    }

    [Fact]
    public async Task A_valid_user_stays_connected_across_checks()
    {
        var sessions = new HubTestSessions();
        var clock = new HubTestClock();
        await using var app = await StartAsync(new HubTestDivisions(), sessions, clock);
        var alice = Guid.NewGuid();
        using var connection = await ConnectAsync(app.GetTestServer(), CreateToken(alice));

        for (var check = 0; check < 3; check++)
        {
            var validated = sessions.NextValidation(alice);
            clock.Advance(Interval);
            (await validated.WaitAsync(Patience)).Should().Be(HubTestSessions.FirstStamp);
        }

        var operation = await PublishAsync(app, alice, null);
        (await NextOperationIdAsync(connection)).Should().Be(operation);
    }

    [Fact]
    public async Task Without_the_check_no_connection_is_watched_and_a_removed_user_keeps_receiving()
    {
        var sessions = new HubTestSessions();
        var clock = new HubTestClock();
        await using var app = await StartAsync(
            new HubTestDivisions(),
            sessions,
            clock,
            options => options.HubSessionCheckEnabled = false);
        var alice = Guid.NewGuid();
        var watched = clock.TimersCreated;
        using var connection = await ConnectAsync(app.GetTestServer(), CreateToken(alice));

        sessions.Remove(alice);
        clock.Advance(Interval);

        clock.TimersCreated.Should().Be(watched, "nothing watches a connection without the check");
        var operation = await PublishAsync(app, alice, null);
        (await NextOperationIdAsync(connection)).Should().Be(operation);
    }

    [Fact]
    public async Task With_a_shorter_interval_the_check_runs_after_that_interval()
    {
        var sessions = new HubTestSessions();
        var clock = new HubTestClock();
        await using var app = await StartAsync(
            new HubTestDivisions(),
            sessions,
            clock,
            options => options.HubSessionCheckInterval = TimeSpan.FromSeconds(15));
        var alice = Guid.NewGuid();
        using var connection = await ConnectAsync(app.GetTestServer(), CreateToken(alice));

        sessions.Remove(alice);
        clock.Advance(TimeSpan.FromSeconds(15));

        await connection.Connection.WaitForCloseAsync();
    }

#if NET11_0_OR_GREATER
    [Fact]
    public async Task After_a_refresh_the_check_validates_the_refreshed_principal()
    {
        var sessions = new HubTestSessions();
        var clock = new HubTestClock();
        await using var app = await StartAsync(
            new HubTestDivisions(),
            sessions,
            clock,
            options => options.HubAuthenticationRefresh = true);
        var server = app.GetTestServer();
        var alice = Guid.NewGuid();
        using var connection = await ConnectAsync(server, CreateToken(alice));

        // Alice changed her password and her client renewed its token with the new security stamp; both
        // stamps count until the hub took the refreshed principal.
        sessions.Accept(alice, HubTestSessions.FirstStamp, "s2");
        using var refreshed = await RefreshAsync(server, connection.ConnectionToken, CreateToken(alice, TimeSpan.FromMinutes(20), "s2"));
        refreshed.StatusCode.Should().Be(HttpStatusCode.OK);
        await WaitUntilCheckedWithAsync(sessions, clock, alice, "s2");

        // From now on only the new stamp counts: the refreshed connection stays.
        sessions.Accept(alice, "s2");
        var validated = sessions.NextValidation(alice);
        clock.Advance(Interval);

        (await validated.WaitAsync(Patience)).Should().Be("s2");
        var operation = await PublishAsync(app, alice, null);
        (await NextOperationIdAsync(connection)).Should().Be(operation);
    }

    [Fact]
    public async Task A_connection_that_did_not_refresh_is_still_checked_with_the_principal_it_connected_with()
    {
        var sessions = new HubTestSessions();
        var clock = new HubTestClock();
        await using var app = await StartAsync(
            new HubTestDivisions(),
            sessions,
            clock,
            options => options.HubAuthenticationRefresh = true);
        var server = app.GetTestServer();
        var alice = Guid.NewGuid();
        var bob = Guid.NewGuid();
        using var connection = await ConnectAsync(server, CreateToken(alice));

        // A refresh with another person's token is refused, so the connection keeps Alice's principal,
        // which the check refuses once she was removed.
        using var refused = await RefreshAsync(server, connection.ConnectionToken, CreateToken(bob, TimeSpan.FromMinutes(20)));
        sessions.Remove(alice);
        clock.Advance(Interval);

        refused.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        await connection.Connection.WaitForCloseAsync();
    }

    /// <summary>
    /// Moves the clock one interval at a time until the check of the user's only connection validated a
    /// principal with <paramref name="stamp"/>. The hub takes an accepted refresh's principal when it
    /// finished its own check of the refresh, which the refresh's response does not wait for; the
    /// rounds before validate the previous principal, which the test still accepts.
    /// </summary>
    private static async Task WaitUntilCheckedWithAsync(HubTestSessions sessions, HubTestClock clock, Guid userId, string stamp)
    {
        for (var round = 1; ; round++)
        {
            var validated = sessions.NextValidation(userId);
            clock.Advance(Interval);
            if (await validated.WaitAsync(Patience) == stamp)
            {
                return;
            }

            round.Should().BeLessThan(20, "the hub takes the refreshed principal once it accepted the refresh");
        }
    }
#endif

    private static Task<WebApplication> StartAsync(
        HubTestDivisions divisions,
        HubTestSessions sessions,
        HubTestClock clock,
        Action<NhBackgroundOperationsOptions>? configure = null)
    {
        return BackgroundOperationHubTestHost.StartAsync(sessions, divisions, configure, clock);
    }
}
