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
    /// Whether this host runs the scheduled triggers (<c>NewHeap:AI:Assistant:RunSchedules</c>), true by
    /// default; read when the host starts. A host that runs them registers a Hangfire recurring job for
    /// every schedule in code when it starts, and keeps the recurring jobs of schedules defined as data in
    /// line with its snapshot: it adds and changes them, and removes those of schedules that were removed
    /// or are out of effect. Set it to false on a host that shares the database, and perhaps the Hangfire
    /// storage, of the scheduling host but does not run the schedules, such as a host without a Hangfire
    /// server: it adds and removes no recurring job and needs no <c>IRecurringJobManager</c>, while
    /// triggers, runs, the administration and the following of data changes work as usual. Keep it true
    /// on the hosts whose Hangfire servers run the schedules, with the same agents and schedules.
    /// </summary>
    public bool RunSchedules { get; set; } = true;

    /// <summary>
    /// Whether a person's turn keeps running after the client that started it disconnected
    /// (<c>NewHeap:AI:Assistant:KeepTurnsAfterDisconnect</c>), false by default; read for every
    /// <c>POST conversations/{id}/messages</c> and <c>POST conversations/{id}/approvals/{approvalId}/decide</c>.
    /// When false, closing the browser cancels the turn, as before. When true, the turn continues until
    /// it ends or reaches <see cref="NhAssistantLimits.TurnTimeout"/>: its tools authorize the principal
    /// and the invocation context captured when it started, never the request again; what it would still
    /// write to the closed stream is dropped; the conversation's snapshot and its live updates carry its
    /// result, so a client that comes back reloads the conversation, as there is no stream to resume.
    /// <c>POST conversations/{id}/cancel</c> still stops it. A turn that
    /// <c>INhAssistantConversationTurns</c> starts follows this setting unless its options say otherwise.
    /// </summary>
    public bool KeepTurnsAfterDisconnect { get; set; }

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
    /// <c>MapNewHeapAssistant</c> on the host's root, outside the route prefix it is given:
    /// <c>/hub/assistant</c> by default (<c>NewHeap:AI:Assistant:HubPath</c>). NewHeap hosts read the
    /// bearer token and active division from the query string, which SignalR needs for WebSockets, for
    /// paths below <c>/hub</c> and for this path wherever it is, such as <c>/hubs/assistant</c> or
    /// <c>/api/assistant/hub</c>. Set it empty to turn live updates off; clients then reload snapshots
    /// instead.
    /// </summary>
    public string? HubPath { get; set; } = DefaultHubPath;

    /// <summary>
    /// Whether compositions are on (<c>NewHeap:AI:Assistant:Compositions</c>), false by default. The
    /// default <see cref="INhAssistantCompositionSwitch"/> reads it once per turn, run step and MCP
    /// request, so a reloaded configuration applies from the next one; an application that registers its
    /// own switch with <c>UseCompositionSwitch</c> decides from its own data instead. Code still has to
    /// allow compositions for an agent (<c>ConfigureAgentContext(...).AllowCompositions()</c>) or an MCP
    /// exposure (<c>AllowCompositions()</c>).
    /// </summary>
    public bool Compositions { get; set; }

    /// <summary>
    /// Whether the script tool is on (<c>NewHeap:AI:Assistant:Scripts</c>), false by default; read as
    /// <see cref="Compositions"/> is. Code still has to allow scripts for an agent or an MCP exposure, and
    /// the scripting package has to be installed.
    /// </summary>
    public bool Scripts { get; set; }
}

/// <summary>
/// Connection rules for remote MCP servers. Link-local addresses and cloud metadata hosts are always
/// blocked, also when they match <see cref="AllowedHosts"/>; loopback is blocked outside Development
/// unless <see cref="AllowLoopback"/> is on.
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
    /// Requires https. Plain http is then only allowed to loopback, in Development or with
    /// <see cref="AllowLoopback"/>.
    /// </summary>
    public bool RequireHttps { get; set; } = true;

    /// <summary>
    /// How long a server's tool list and connection are reused.
    /// </summary>
    public TimeSpan ToolListCacheDuration { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Maximum time to connect to a server, and maximum time to list its tools once connected.
    /// </summary>
    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// The shortest <see cref="ReaderCallTimeout"/>.
    /// </summary>
    internal static readonly TimeSpan MinReaderCallTimeout = TimeSpan.FromSeconds(1);

    /// <summary>
    /// The longest <see cref="ReaderCallTimeout"/>.
    /// </summary>
    internal static readonly TimeSpan MaxReaderCallTimeout = TimeSpan.FromMinutes(15);

    /// <summary>
    /// The longest an MCP server set up as reader may take to turn one file into text: 60 seconds by
    /// default, from one second to 15 minutes (<c>NewHeap:AI:Assistant:Mcp:ReaderCallTimeout</c>). A
    /// reader that has not answered by then fails the file with <c>assistant-file-reader-failed</c>.
    /// The turn's own deadline and cancellation still apply.
    /// </summary>
    public TimeSpan ReaderCallTimeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>The smallest <see cref="MaxResponseBytes"/>.</summary>
    internal const int MinResponseBytes = 64 * 1024;

    /// <summary>The largest <see cref="MaxResponseBytes"/>.</summary>
    internal const int MaxResponseBytesLimit = 256 * 1024 * 1024;

    /// <summary>
    /// The largest response the host reads from an MCP server, in bytes: a JSON response, or one event
    /// of an event stream. 16 MiB by default, from 64 KiB to 256 MiB
    /// (<c>NewHeap:AI:Assistant:Mcp:MaxResponseBytes</c>). The host stops reading a longer response
    /// before it holds more, and its request fails with <c>assistant-mcp-response-too-large</c>; the
    /// result and text limits of tools and readers then apply to what is left. Raise it only with
    /// <see cref="NhAssistantLimits.MaxFileTokensPerTurn"/> for readers whose text is longer.
    /// </summary>
    public int MaxResponseBytes { get; set; } = 16 * 1024 * 1024;

    /// <summary>The largest <see cref="MaxToolListPages"/>.</summary>
    internal const int MaxToolListPagesLimit = 1_000;

    /// <summary>
    /// The most pages of <c>tools/list</c> the host reads from one server: 20 by default, from 1 to
    /// 1,000 (<c>NewHeap:AI:Assistant:Mcp:MaxToolListPages</c>). A server that announces more is not
    /// connected, for turns, readers, tests and syncs alike, with
    /// <c>assistant-mcp-tool-list-too-large</c>.
    /// </summary>
    public int MaxToolListPages { get; set; } = 20;

    /// <summary>The largest <see cref="MaxTools"/>.</summary>
    internal const int MaxToolsLimit = 10_000;

    /// <summary>
    /// The most tools the host takes from one server's <c>tools/list</c>: 500 by default, from 1 to
    /// 10,000 (<c>NewHeap:AI:Assistant:Mcp:MaxTools</c>). A server that lists more is not connected,
    /// and a sync stores none of them, with <c>assistant-mcp-tool-list-too-large</c>.
    /// </summary>
    public int MaxTools { get; set; } = 500;

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
    /// Lets MCP servers and the OAuth URLs they name use loopback (<c>localhost</c>, <c>127.0.0.0/8</c>
    /// and <c>::1</c>), also over plain http, in an environment other than Development, such as a test
    /// host of the environment <c>Testing</c> with an MCP server on <c>127.0.0.1</c>; the OAuth redirect
    /// URI and return origins may then be http on loopback too. Off by default; the host does not start
    /// in Production when it is on. Link-local addresses and cloud metadata hosts stay blocked
    /// (<c>NewHeap:AI:Assistant:Mcp:AllowLoopback</c>).
    /// </summary>
    public bool AllowLoopback { get; set; }

    /// <summary>
    /// The host as OAuth client of MCP authorization servers.
    /// </summary>
    public NhAssistantMcpOAuthOptions OAuth { get; set; } = new();

    /// <summary>
    /// Throws when a bound of the MCP connections is out of its range, so the host fails at start
    /// instead of at the first request.
    /// </summary>
    internal void Validate()
    {
        if (ReaderCallTimeout < MinReaderCallTimeout || ReaderCallTimeout > MaxReaderCallTimeout)
        {
            throw new InvalidOperationException(
                "NewHeap:AI:Assistant:Mcp:ReaderCallTimeout must be from one second to 15 minutes.");
        }
        if (MaxResponseBytes is < MinResponseBytes or > MaxResponseBytesLimit)
        {
            throw new InvalidOperationException(
                "NewHeap:AI:Assistant:Mcp:MaxResponseBytes must be from 64 KiB to 256 MiB.");
        }
        if (MaxToolListPages is < 1 or > MaxToolListPagesLimit)
        {
            throw new InvalidOperationException(
                "NewHeap:AI:Assistant:Mcp:MaxToolListPages must be from 1 to 1,000.");
        }
        if (MaxTools is < 1 or > MaxToolsLimit)
        {
            throw new InvalidOperationException(
                "NewHeap:AI:Assistant:Mcp:MaxTools must be from 1 to 10,000.");
        }
    }

    /// <summary>
    /// <see cref="MaxResponseBytes"/> within its range, also after a change at runtime.
    /// </summary>
    internal int BoundedMaxResponseBytes()
    {
        return Math.Clamp(MaxResponseBytes, MinResponseBytes, MaxResponseBytesLimit);
    }

    /// <summary>
    /// <see cref="MaxToolListPages"/> within its range, also after a change at runtime.
    /// </summary>
    internal int BoundedMaxToolListPages()
    {
        return Math.Clamp(MaxToolListPages, 1, MaxToolListPagesLimit);
    }

    /// <summary>
    /// <see cref="MaxTools"/> within its range, also after a change at runtime.
    /// </summary>
    internal int BoundedMaxTools()
    {
        return Math.Clamp(MaxTools, 1, MaxToolsLimit);
    }

    /// <summary>
    /// <see cref="ReaderCallTimeout"/> within its range: a value changed after start is never longer
    /// than the longest or shorter than the shortest.
    /// </summary>
    internal TimeSpan BoundedReaderCallTimeout()
    {
        return ReaderCallTimeout < MinReaderCallTimeout
            ? MinReaderCallTimeout
            : ReaderCallTimeout > MaxReaderCallTimeout
                ? MaxReaderCallTimeout
                : ReaderCallTimeout;
    }
}

/// <summary>
/// The host as OAuth client of MCP authorization servers (<c>NewHeap:AI:Assistant:Mcp:OAuth</c>).
/// </summary>
public sealed class NhAssistantMcpOAuthOptions
{
    /// <summary>
    /// The public absolute URL of the assistant's OAuth callback, <c>&lt;prefix&gt;/mcp/oauth/callback</c>.
    /// Required for servers with <c>oauth</c>; https, or http on loopback in Development or with
    /// <see cref="NhAssistantMcpOptions.AllowLoopback"/>.
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
                $"NewHeap:AI:Assistant:Mcp:OAuth:{name} must be an absolute https URL without a fragment (http only on loopback, in Development or with Mcp:AllowLoopback).");
        }
    }

    /// <summary>A loopback address, <c>localhost</c> or a <c>.localhost</c> name (RFC 6761).</summary>
    private static bool IsLoopback(Uri uri)
    {
        return uri.IsLoopback || uri.IdnHost.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase);
    }
}
