using System.Security.Claims;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace NewHeap.Platform.AspNet.Common.Authentication;

/// <summary>
/// Ends the connections of a SignalR hub that a NewHeap library maps once their session ended. Such a
/// hub closes a connection when the authentication it was opened with expires
/// (<see cref="NhHubAuthenticationRefresh"/>), but a session can end before that: a password change or
/// reset, or the revocation of a user's sessions, changes the user's security stamp, a deleted user no
/// longer exists, and an impersonation ends with its origin's session. At every check interval this
/// monitor therefore validates the principal of each watched connection again with the NewHeap session
/// validator (<see cref="INhAuthenticationSessionValidator"/>), which the bearer authentication applies
/// to every request, and aborts a connection it refuses; other connections stay open. The interval
/// follows the application's clock (a registered <see cref="TimeProvider"/>, otherwise the system
/// clock), so a test moves its clock instead of waiting. A host without the validator, such as one with
/// its own authentication, relies on the expiry alone. A check that fails is logged and ends the
/// connection as well; the client reconnects with its current session and resynchronizes. A connection
/// whose authentication was refreshed on ASP.NET Core 11 is validated with its refreshed principal once
/// the hub accepted it (<see cref="Refreshed"/>).
/// </summary>
internal sealed partial class NhHubSessions(
    IServiceScopeFactory scopes,
    IServiceProviderIsService registrations,
    ILogger<NhHubSessions> logger,
    TimeProvider? timeProvider = null)
{
    /// <summary>How often a library hub validates the session of an open connection again by default.</summary>
    public static readonly TimeSpan DefaultCheckInterval = TimeSpan.FromMinutes(1);

    private const string SessionKey = "nh-hub-session";

    private readonly TimeProvider _applicationClock = timeProvider ?? TimeProvider.System;

    /// <summary>
    /// Validates the session of <paramref name="connection"/> again every
    /// <paramref name="checkInterval"/> until the connection closes, and aborts the connection once the
    /// session is no longer valid. Call it once the connection joined its groups. A connection without
    /// an authenticated principal, or a host without the session validator, is not watched.
    /// </summary>
    /// <param name="connection">The connection of the hub that opens.</param>
    /// <param name="hub">The hub's name, for the log.</param>
    /// <param name="checkInterval">How often the session is validated again; positive.</param>
    public void Watch(HubCallerContext connection, string hub, TimeSpan checkInterval)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(hub);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(checkInterval, TimeSpan.Zero);
        if (connection.User is not { Identity.IsAuthenticated: true } user
            || !registrations.IsService(typeof(INhAuthenticationSessionValidator)))
        {
            return;
        }

        var session = new WatchedSession(user);
        connection.Items[SessionKey] = session;
        // The timer is created before Watch returns, so the first check is due one interval from now.
        _ = WatchAsync(connection, session, hub, checkInterval);
    }

#if NET11_0_OR_GREATER
    /// <summary>
    /// Validates <paramref name="principal"/>, the refreshed principal of <paramref name="connection"/>,
    /// from now on, after the hub accepted its authentication refresh. A connection that is not watched
    /// stays unwatched.
    /// </summary>
    public void Refreshed(HubCallerContext connection, ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(principal);
        if (principal.Identity?.IsAuthenticated == true
            && connection.Items.TryGetValue(SessionKey, out var value)
            && value is WatchedSession session)
        {
            session.Principal = principal;
        }
    }
#endif

    private async Task WatchAsync(
        HubCallerContext connection,
        WatchedSession session,
        string hub,
        TimeSpan checkInterval)
    {
        try
        {
            using var timer = new PeriodicTimer(checkInterval, _applicationClock);
            while (await timer.WaitForNextTickAsync(connection.ConnectionAborted))
            {
                if (!await IsValidAsync(session.Principal, hub, connection.ConnectionAborted))
                {
                    LogSessionEnded(logger, hub);
                    connection.Abort();
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // The connection closed.
        }
    }

    private async Task<bool> IsValidAsync(ClaimsPrincipal user, string hub, CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            return await scope.ServiceProvider
                .GetRequiredService<INhAuthenticationSessionValidator>()
                .ValidateAsync(user, cancellationToken);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException
            && !(exception is OperationCanceledException && cancellationToken.IsCancellationRequested))
        {
            // A validator that times out ends the connection as any other failed check does; only the
            // connection's own closing stops the watch.
            LogCheckFailed(logger, hub, exception);
            return false;
        }
    }

    /// <summary>
    /// The principal a connection's session is validated with: the one it connected with, or the one
    /// its last accepted authentication refresh brought.
    /// </summary>
    private sealed class WatchedSession(ClaimsPrincipal principal)
    {
        private ClaimsPrincipal _principal = principal;

        public ClaimsPrincipal Principal
        {
            get => Volatile.Read(ref _principal);
            set => Volatile.Write(ref _principal, value);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "A connection of {Hub} was closed because its session is no longer valid.")]
    private static partial void LogSessionEnded(ILogger logger, string hub);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The session of a connection of {Hub} could not be validated; the connection is closed and the client reconnects.")]
    private static partial void LogCheckFailed(ILogger logger, string hub, Exception exception);
}
