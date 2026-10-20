namespace NewHeap.Platform.AI.Chat;

/// <summary>
/// Configuration bound from <c>NewHeap:AI:Assistant</c>.
/// </summary>
public sealed class NhAssistantOptions
{
    public const string SectionName = "NewHeap:AI:Assistant";
    public const string DefaultAccessPolicy = "app.assistant.access";

    /// <summary>
    /// Kill switch. When false, <c>GET status</c> reports <c>enabled: false</c> without agents and
    /// every other assistant endpoint returns <c>404</c>. Defaults to false.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Authorization policy every assistant endpoint requires unless
    /// <c>NhAssistantBuilder.UseAccessPolicy</c> sets one explicitly.
    /// </summary>
    public string AccessPolicy { get; set; } = DefaultAccessPolicy;

    public const string DefaultAdminPolicy = "app.assistant.admin";

    /// <summary>
    /// Authorization policy the administration endpoints require in addition to the access policy,
    /// unless <c>NhAssistantBuilder.UseAdminPolicy</c> sets one explicitly.
    /// </summary>
    public string AdminPolicy { get; set; } = DefaultAdminPolicy;

    /// <summary>
    /// Connection rules for administrator-connected MCP servers (<c>NewHeap:AI:Assistant:Mcp</c>).
    /// </summary>
    public NhAssistantMcpOptions Mcp { get; set; } = new();

    /// <summary>
    /// Web Push notifications for people who are away (<c>NewHeap:AI:Assistant:Push</c>).
    /// </summary>
    public NhAssistantPushOptions Push { get; set; } = new();

    public const string DefaultHubPath = "/hub/assistant";

    /// <summary>
    /// Path of the SignalR hub that sends live updates of conversations, mapped by
    /// <c>MapNewHeapAssistant</c>. NewHeap hosts read the bearer token and active division from the
    /// query string for paths below <c>/hub</c>, which SignalR needs for WebSockets. Set it empty to
    /// turn live updates off; clients then reload snapshots instead.
    /// </summary>
    public string? HubPath { get; set; } = DefaultHubPath;
}

/// <summary>
/// Connection rules for remote MCP servers. Link-local addresses, cloud metadata hosts and, outside
/// Development, loopback are always blocked, also when they match <see cref="AllowedHosts"/>.
/// </summary>
public sealed class NhAssistantMcpOptions
{
    /// <summary>
    /// Host names or <c>*.</c> suffix patterns a server URL may use. Empty allows every host that is
    /// not blocked.
    /// </summary>
    public IList<string> AllowedHosts { get; set; } = [];

    /// <summary>
    /// Exact host names that may receive the caller's bearer token (<c>forward-user-token</c>).
    /// </summary>
    public IList<string> ForwardUserTokenHosts { get; set; } = [];

    /// <summary>
    /// Requires https. Plain http is then only allowed to loopback in Development.
    /// </summary>
    public bool RequireHttps { get; set; } = true;

    /// <summary>
    /// How long a server's tool list and connection are reused.
    /// </summary>
    public TimeSpan ToolListCacheDuration { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Maximum time to connect to a server and list its tools.
    /// </summary>
    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// The lifetime of a delegated NewHeap token that a headless run sends to a
    /// <c>forward-user-token</c> server for the person it acts for: 2 minutes by default, at most 5.
    /// </summary>
    public TimeSpan ForwardedTokenLifetime { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Whether MCP endpoints in private networks (10/8, 172.16/12, 192.168/16, 100.64/10 and IPv6
    /// unique local addresses) are blocked too. OAuth URLs, which come from remote documents, always
    /// block them. Hosts in <see cref="AllowedHosts"/> stay reachable.
    /// </summary>
    public bool BlockPrivateNetworks { get; set; }

    /// <summary>
    /// The host as OAuth client of MCP authorization servers.
    /// </summary>
    public NhAssistantMcpOAuthOptions OAuth { get; set; } = new();
}

/// <summary>
/// The host as OAuth client of MCP authorization servers (<c>NewHeap:AI:Assistant:Mcp:OAuth</c>).
/// </summary>
public sealed class NhAssistantMcpOAuthOptions
{
    /// <summary>
    /// The public absolute URL of the assistant's OAuth callback, <c>&lt;prefix&gt;/mcp/oauth/callback</c>.
    /// Required for servers with <c>oauth</c>; https, or http on loopback in Development.
    /// </summary>
    public string? RedirectUri { get; set; }

    /// <summary>
    /// The public absolute URL of this host's Client ID Metadata Document,
    /// <c>&lt;prefix&gt;/mcp/oauth/client-metadata.json</c>. Optional; when set, an authorization server
    /// that supports such documents identifies the host by it instead of a registration.
    /// </summary>
    public string? ClientMetadataDocumentUri { get; set; }

    /// <summary>The name the host registers with and shows on consent screens.</summary>
    public string ClientName { get; set; } = "NewHeap assistant";

    /// <summary>The host's home page in its client metadata. Optional.</summary>
    public string? ClientUri { get; set; }

    /// <summary>
    /// Origins the callback page may post to or return to. Empty uses the redirect URI's origin.
    /// </summary>
    public IList<string> ReturnOrigins { get; set; } = [];

    /// <summary>How long an authorization may take, from start to completion. At most an hour.</summary>
    public TimeSpan FlowLifetime { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>A token this close to its expiry is renewed before use.</summary>
    public TimeSpan RefreshSkew { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>How long one instance holds the right to renew a connection's tokens.</summary>
    public TimeSpan RefreshLeaseDuration { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>A personal connection unused for this long is removed.</summary>
    public TimeSpan ConnectionRetention { get; set; } = TimeSpan.FromDays(90);

    /// <summary>The number of unfinished authorizations one person may have.</summary>
    public int MaxPendingFlowsPerActor { get; set; } = 5;

    /// <summary>
    /// Throws when configured values are invalid: the URLs must be absolute https (http on loopback
    /// only when <paramref name="allowLoopbackHttp"/>), the return origins bare origins and the
    /// durations bounded. Unconfigured OAuth stays off without an error.
    /// </summary>
    internal void Validate(bool allowLoopbackHttp)
    {
        if (FlowLifetime <= TimeSpan.Zero
            || FlowLifetime > TimeSpan.FromHours(1)
            || RefreshSkew < TimeSpan.Zero
            || RefreshSkew > TimeSpan.FromMinutes(10)
            || RefreshLeaseDuration < TimeSpan.FromSeconds(5)
            || RefreshLeaseDuration > TimeSpan.FromMinutes(5)
            || ConnectionRetention < TimeSpan.FromDays(1)
            || MaxPendingFlowsPerActor is < 1 or > 50)
        {
            throw new InvalidOperationException(
                "NewHeap:AI:Assistant:Mcp:OAuth limits must be bounded: FlowLifetime up to an hour, RefreshSkew up to 10 minutes, RefreshLeaseDuration 5 seconds to 5 minutes, ConnectionRetention at least a day and MaxPendingFlowsPerActor 1 to 50.");
        }
        if (string.IsNullOrWhiteSpace(ClientName) || ClientName.Length > 200)
        {
            throw new InvalidOperationException("NewHeap:AI:Assistant:Mcp:OAuth:ClientName must be set and at most 200 characters.");
        }
        CheckUrl(RedirectUri, nameof(RedirectUri), allowLoopbackHttp);
        CheckUrl(ClientMetadataDocumentUri, nameof(ClientMetadataDocumentUri), allowLoopbackHttp: false);
        CheckUrl(ClientUri, nameof(ClientUri), allowLoopbackHttp);
        if (ClientMetadataDocumentUri is not null && RedirectUri is null)
        {
            throw new InvalidOperationException("NewHeap:AI:Assistant:Mcp:OAuth:ClientMetadataDocumentUri needs RedirectUri.");
        }
        foreach (var origin in ReturnOrigins)
        {
            CheckUrl(origin, nameof(ReturnOrigins), allowLoopbackHttp);
            var uri = new Uri(origin);
            if (uri.AbsolutePath != "/" || uri.Query.Length > 0 || !string.IsNullOrEmpty(uri.UserInfo))
            {
                throw new InvalidOperationException("NewHeap:AI:Assistant:Mcp:OAuth:ReturnOrigins must be origins such as https://app.example.com, without a path.");
            }
        }
    }

    private static void CheckUrl(string? value, string name, bool allowLoopbackHttp)
    {
        if (value is null)
        {
            return;
        }
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || uri.Fragment.Length > 0
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !(uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && allowLoopbackHttp && IsLoopback(uri))))
        {
            throw new InvalidOperationException(
                $"NewHeap:AI:Assistant:Mcp:OAuth:{name} must be an absolute https URL without a fragment (http only on loopback in Development).");
        }
    }

    /// <summary>A loopback address, <c>localhost</c> or a <c>.localhost</c> name (RFC 6761).</summary>
    private static bool IsLoopback(Uri uri)
    {
        return uri.IsLoopback || uri.IdnHost.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase);
    }
}
