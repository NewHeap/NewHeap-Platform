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
}
