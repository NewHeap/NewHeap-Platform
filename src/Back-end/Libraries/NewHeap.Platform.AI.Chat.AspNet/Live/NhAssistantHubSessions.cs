using System.Security.Claims;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NewHeap.Platform.AspNet.Common.Authentication;

namespace NewHeap.Platform.AI.Chat.AspNet.Live;

/// <summary>
/// Ends assistant hub connections whose session ended. The hub is mapped with
/// <c>CloseOnAuthenticationExpiration</c>, so a connection closes when the token it was opened with
/// expires. A session can also end before that: a password change or reset changes the user's security
/// stamp, a deleted user no longer exists, and an impersonation ends with its origin's session. Every
/// <see cref="CheckInterval"/> this monitor therefore validates the principal of each open connection
/// again with the NewHeap session validator (<see cref="INhAuthenticationSessionValidator"/>), which the
/// bearer authentication applies to every request, and aborts a connection it refuses. A host without
/// the validator, such as one with its own authentication, relies on the expiry alone. A check that
/// fails is logged and ends the connection as well; the client reconnects with its current session and
/// reloads its snapshots. A connection whose authentication was refreshed
/// (<see cref="NhAssistantOptions.HubAuthenticationRefresh"/>, ASP.NET Core 11) is validated with its
/// refreshed principal from then on.
/// </summary>
internal sealed partial class NhAssistantHubSessions(
    IServiceScopeFactory scopes,
    IServiceProviderIsService registrations,
    ILogger<NhAssistantHubSessions> logger)
{
    /// <summary>How often an open connection's session is validated again by default.</summary>
    public static readonly TimeSpan DefaultCheckInterval = TimeSpan.FromMinutes(1);

    private const string SessionKey = "nh-assistant-session";

    /// <summary>
    /// How often an open connection's session is validated again: <see cref="DefaultCheckInterval"/>,
    /// or less in a test.
    /// </summary>
    internal TimeSpan CheckInterval { get; init; } = DefaultCheckInterval;

    /// <summary>
    /// Validates the session of <paramref name="connection"/> again every interval until the
    /// connection closes, and aborts the connection once the session is no longer valid.
    /// </summary>
    public void Watch(HubCallerContext connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (connection.User is not { Identity.IsAuthenticated: true } user
            || !registrations.IsService(typeof(INhAuthenticationSessionValidator)))
        {
            return;
        }

        var session = new WatchedSession(user);
        connection.Items[SessionKey] = session;
        _ = WatchAsync(connection, session);
    }

#if NET11_0_OR_GREATER
    /// <summary>
    /// Validates the refreshed principal of <paramref name="connection"/> from now on, after the hub
    /// accepted its authentication refresh.
    /// </summary>
    public void Refreshed(HubCallerContext connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (connection.User is { Identity.IsAuthenticated: true } user
            && connection.Items.TryGetValue(SessionKey, out var value)
            && value is WatchedSession session)
        {
            session.Principal = user;
        }
    }
#endif

    private async Task WatchAsync(HubCallerContext connection, WatchedSession session)
    {
        try
        {
            using var timer = new PeriodicTimer(CheckInterval);
            while (await timer.WaitForNextTickAsync(connection.ConnectionAborted))
            {
                if (!await IsValidAsync(session.Principal, connection.ConnectionAborted))
                {
                    LogSessionEnded(logger);
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

    private async Task<bool> IsValidAsync(ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        try
        {
            return await scope.ServiceProvider
                .GetRequiredService<INhAuthenticationSessionValidator>()
                .ValidateAsync(user, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
        {
            LogCheckFailed(logger, exception);
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

    [LoggerMessage(Level = LogLevel.Information, Message = "An assistant hub connection was closed because its session is no longer valid.")]
    private static partial void LogSessionEnded(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The session of an assistant hub connection could not be validated; the connection is closed and the client reconnects.")]
    private static partial void LogCheckFailed(ILogger logger, Exception exception);
}
