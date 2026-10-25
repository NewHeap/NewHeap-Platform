using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.AspNet.Common.Services.BackgroundOperations;

namespace NewHeap.Platform.AspNet.Common.Authentication;

/// <summary>
/// A SignalR hub that a NewHeap library maps, such as the assistant's. Its requests may carry the
/// bearer token and the active division in the query string, as WebSockets and server-sent events in
/// a browser need. Only the libraries register one; an application's own hubs keep the <c>/hub</c>
/// prefix.
/// </summary>
internal interface INhLibraryHubPath
{
    /// <summary>
    /// The hub's absolute application path as it is mapped, such as <c>/hub/assistant</c>;
    /// <see langword="null"/> or empty while the hub is not mapped.
    /// </summary>
    string? HubPath { get; }
}

/// <summary>
/// Reads the bearer token (<c>access_token</c>) and the active division (<c>divisionId</c>) from the
/// query string of a SignalR request: below <c>/hub</c>, as before, and below the configured path of a
/// hub a NewHeap library maps (the background operations' hub and every <see cref="INhLibraryHubPath"/>),
/// so a hub moved to <c>/hubs/assistant</c> or under a route prefix keeps working. Any other request
/// takes its token from the <c>Authorization</c> header only, so tokens do not reach URLs and logs.
/// </summary>
internal static class NhHubQueryTokens
{
    /// <summary>
    /// The prefix below which every request may carry its token in the query string.
    /// </summary>
    public const string HubPrefix = "/hub";

    public const string AccessTokenParameter = "access_token";

    public const string DivisionParameter = "divisionId";

    public static void Read(MessageReceivedContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!Applies(context.HttpContext))
        {
            return;
        }

        var accessToken = context.Request.Query[AccessTokenParameter];
        if (!string.IsNullOrEmpty(accessToken))
        {
            context.Token = accessToken;
        }

        var divisionId = context.Request.Query[DivisionParameter];
        if (!string.IsNullOrEmpty(divisionId))
        {
            context.Request.Headers[Constants.HttpHeaderKeys.ActiveDivisionId] = divisionId;
        }
    }

    /// <summary>
    /// Whether the request is one of a hub whose token may travel in the query string.
    /// </summary>
    public static bool Applies(HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        var path = httpContext.Request.Path;
        if (path.StartsWithSegments(HubPrefix))
        {
            return true;
        }

        var services = httpContext.RequestServices;
        if (services.GetService<NhBackgroundOperationSignalRMarker>() is not null
            && IsBelow(path, services.GetService<NhBackgroundOperationsOptions>()?.HubPath))
        {
            return true;
        }

        foreach (var hub in services.GetServices<INhLibraryHubPath>())
        {
            if (IsBelow(path, hub.HubPath))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether <paramref name="path"/> is the hub path or below it. The root or a relative path names
    /// no hub, so it never opens the query string to every request.
    /// </summary>
    private static bool IsBelow(PathString path, string? hubPath)
    {
        if (string.IsNullOrWhiteSpace(hubPath) || !hubPath.StartsWith('/'))
        {
            return false;
        }

        var trimmed = hubPath.TrimEnd('/');
        return trimmed.Length > 0 && path.StartsWithSegments(new PathString(trimmed));
    }
}
