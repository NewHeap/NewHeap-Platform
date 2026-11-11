using System.Security.Claims;
using Microsoft.AspNetCore.Http.Connections;
using NewHeap.Platform.Common.Identity.Claims;
#if NET11_0_OR_GREATER
using Microsoft.AspNetCore.Connections.Features;
#endif

namespace NewHeap.Platform.AspNet.Common.Authentication;

/// <summary>
/// How a SignalR hub that a NewHeap library maps treats the authentication of its connections. A
/// connection always closes when the authentication it was opened with expires
/// (<see cref="HttpConnectionDispatcherOptions.CloseOnAuthenticationExpiration"/>), so it never lives on
/// through keep-alives; the client reconnects with its current session. With the library's opt-in on
/// ASP.NET Core 11 the hub also maps SignalR's <c>/refresh</c> endpoint: the client presents a renewed
/// token, the endpoint authenticates and authorizes it as it does the hub, and the connection keeps its
/// groups with the new principal and the new expiry instead of reconnecting. On ASP.NET Core 10 the
/// opt-in changes nothing.
/// </summary>
internal static class NhHubAuthenticationRefresh
{
    /// <summary>
    /// Configures the connection endpoints of a library hub. <paramref name="authenticationRefresh"/>
    /// is the library's opt-in; it takes effect on ASP.NET Core 11 only.
    /// </summary>
    public static void Configure(HttpConnectionDispatcherOptions connection, bool authenticationRefresh)
    {
        ArgumentNullException.ThrowIfNull(connection);
        connection.CloseOnAuthenticationExpiration = true;
#if NET11_0_OR_GREATER
        if (authenticationRefresh)
        {
            // The refresh endpoint carries the hub's authorization metadata, so an expired token or a
            // removed user is refused there with 401 and the session validator applies as on every
            // request. SignalR keeps the subject of the connection; this policy adds NewHeap's person.
            connection.EnableAuthenticationRefresh = true;
            connection.OnAuthenticationRefresh = AcceptSamePersonAsync;
        }
#endif
    }

#if NET11_0_OR_GREATER
    /// <summary>
    /// Accepts a refreshed principal only for the person the connection was opened for: the same user
    /// id and the same impersonation origin. A token of another person, such as after an account switch,
    /// or one that starts or ends an impersonation, is refused with 403 and leaves the connection as it
    /// was until its own authentication expires; the client opens a new connection for that person.
    /// </summary>
    private static Task<bool> AcceptSamePersonAsync(AuthenticationRefreshContext context)
    {
        return Task.FromResult(IsSamePerson(context.PreviousUser, context.NewUser));
    }
#endif

    /// <summary>
    /// Whether <paramref name="refreshed"/> is the same person as <paramref name="current"/>: both
    /// authenticated, with the same user id (<see cref="ClaimTypes.NameIdentifier"/>) and the same
    /// impersonation origin (<see cref="NhPlatformClaimTypes.ImpersonateOriginUserId"/>), compared
    /// ordinally. A principal without a user id is the same person only as another one without.
    /// </summary>
    internal static bool IsSamePerson(ClaimsPrincipal current, ClaimsPrincipal refreshed)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(refreshed);
        if (current.Identity?.IsAuthenticated != true || refreshed.Identity?.IsAuthenticated != true)
        {
            return false;
        }

        return string.Equals(
                current.FindFirstValue(ClaimTypes.NameIdentifier),
                refreshed.FindFirstValue(ClaimTypes.NameIdentifier),
                StringComparison.Ordinal)
            && string.Equals(
                current.FindFirstValue(NhPlatformClaimTypes.ImpersonateOriginUserId),
                refreshed.FindFirstValue(NhPlatformClaimTypes.ImpersonateOriginUserId),
                StringComparison.Ordinal);
    }
}
