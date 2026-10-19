using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Claims;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Client;
using NewHeap.Platform.AI.AspNet;
using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.AI.Chat.Runtime;
using NewHeap.Platform.AI.Mcp;
using NewHeap.Platform.Common.Models;

namespace NewHeap.Platform.AI.Chat.AspNet.Mcp;

/// <summary>
/// Protects MCP secrets with ASP.NET Data Protection. Plain secrets never leave this class except
/// as an outgoing request header.
/// </summary>
internal sealed class NhAssistantMcpSecretProtector(IDataProtectionProvider provider)
{
    public const string Purpose = "NewHeap.AI.Assistant.Mcp";

    private readonly IDataProtector _protector = provider.CreateProtector(Purpose);

    public string Protect(string secret)
    {
        return _protector.Protect(secret);
    }

    public string? Unprotect(string? protectedSecret)
    {
        if (string.IsNullOrEmpty(protectedSecret))
        {
            return null;
        }
        try
        {
            return _protector.Unprotect(protectedSecret);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            // The key ring no longer contains the key; the server needs a new secret.
            return null;
        }
    }
}

/// <summary>
/// A validated connection target: the endpoint, the credential headers at planning and the source
/// that the HTTP client asks again before every request.
/// </summary>
internal sealed record NhAssistantMcpConnectionPlan(
    string ServerId,
    Uri Endpoint,
    IReadOnlyDictionary<string, string> Headers,
    bool IsPerCaller)
{
    /// <summary>
    /// The credential source of the connection; <see langword="null"/> only for a plan built by hand
    /// in a test, which then sends <see cref="Headers"/>.
    /// </summary>
    public NhAssistantMcpCredentialSource? Credentials { get; init; }

    /// <summary>
    /// One of <see cref="NhAssistantMcpTransports"/>.
    /// </summary>
    public string Transport { get; init; } = NhAssistantMcpTransports.StreamableHttp;

    /// <summary>
    /// The client certificate for mutual TLS, or <see langword="null"/>.
    /// </summary>
    public X509Certificate2? ClientCertificate { get; init; }
}

/// <summary>
/// SSRF rules for MCP servers: https unless loopback in Development, the optional host allow-list,
/// blocked metadata names and blocked address ranges after DNS resolution.
/// </summary>
internal sealed class NhAssistantMcpHostGuard(
    IOptionsMonitor<NhAssistantOptions> options,
    IHostEnvironment environment)
{
    private static readonly string[] MetadataHosts =
    [
        "metadata",
        "metadata.google.internal",
        "metadata.goog",
        "instance-data",
        "instance-data.ec2.internal"
    ];

    private static readonly IPAddress AwsIpv6Metadata = IPAddress.Parse("fd00:ec2::254");

    public bool AllowsLoopback => environment.IsDevelopment();

    /// <summary>
    /// Returns an error code when the URL or host name is not allowed, otherwise null.
    /// </summary>
    public string? CheckUri(Uri uri)
    {
        var mcp = options.CurrentValue.Mcp;
        if (!uri.IsAbsoluteUri || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            return NhAssistantAdminErrorCodes.McpHostBlocked;
        }
        var host = uri.IdnHost.TrimEnd('.').ToLowerInvariant();
        var loopbackName = host == "localhost" || (IPAddress.TryParse(host, out var literal) && IPAddress.IsLoopback(literal));
        if (uri.Scheme == Uri.UriSchemeHttp && mcp.RequireHttps && !(loopbackName && AllowsLoopback))
        {
            return NhAssistantAdminErrorCodes.McpHostBlocked;
        }
        if (MetadataHosts.Contains(host, StringComparer.Ordinal) || (loopbackName && !AllowsLoopback))
        {
            return NhAssistantAdminErrorCodes.McpHostBlocked;
        }
        if (IPAddress.TryParse(host, out var address) && IsBlocked(address))
        {
            return NhAssistantAdminErrorCodes.McpHostBlocked;
        }
        if (mcp.AllowedHosts.Count > 0 && !mcp.AllowedHosts.Any(pattern => MatchesHost(pattern, host)))
        {
            return NhAssistantAdminErrorCodes.McpHostBlocked;
        }
        return null;
    }

    /// <summary>
    /// Returns an error code when a request of a connection may not go to <paramref name="target"/>:
    /// it must stay on the origin of the planned endpoint and pass <see cref="CheckUri"/>, including
    /// <see cref="NhAssistantMcpOptions.AllowedHosts"/> as they are now.
    /// </summary>
    public string? CheckRequest(Uri? target, Uri endpoint)
    {
        if (target is null || !target.IsAbsoluteUri)
        {
            return NhAssistantAdminErrorCodes.McpHostBlocked;
        }
        var sameOrigin = string.Equals(target.Scheme, endpoint.Scheme, StringComparison.OrdinalIgnoreCase)
            && string.Equals(target.IdnHost, endpoint.IdnHost, StringComparison.OrdinalIgnoreCase)
            && target.Port == endpoint.Port;
        if (!sameOrigin)
        {
            return NhAssistantAdminErrorCodes.McpHostBlocked;
        }
        return CheckUri(target);
    }

    public bool IsBlocked(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }
        if (address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any) || address.Equals(IPAddress.None))
        {
            return true;
        }
        if (IPAddress.IsLoopback(address))
        {
            return !AllowsLoopback;
        }
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = address.GetAddressBytes();
            // 169.254.0.0/16 link-local, including the cloud metadata address 169.254.169.254.
            return bytes[0] == 169 && bytes[1] == 254;
        }
        return address.IsIPv6LinkLocal || address.Equals(AwsIpv6Metadata);
    }

    /// <summary>
    /// Resolves the host and returns the addresses that may be contacted; empty when all are blocked.
    /// </summary>
    public async Task<IReadOnlyList<IPAddress>> ResolveAllowedAsync(string host, CancellationToken cancellationToken)
    {
        var addresses = IPAddress.TryParse(host, out var literal)
            ? [literal]
            : await Dns.GetHostAddressesAsync(host, cancellationToken);
        if (addresses.Any(IsBlocked))
        {
            // One blocked answer blocks the host, so DNS answers cannot mix in an internal target.
            return [];
        }
        return addresses;
    }

    public static bool MatchesHost(string pattern, string host)
    {
        var normalized = pattern.Trim().TrimEnd('.').ToLowerInvariant();
        if (normalized.StartsWith("*.", StringComparison.Ordinal))
        {
            return host.EndsWith(normalized[1..], StringComparison.Ordinal) && host.Length > normalized.Length - 1;
        }
        return string.Equals(normalized, host, StringComparison.Ordinal);
    }
}

/// <summary>
/// Builds the connection plan of a server: SSRF checks and the credential source of its mode.
/// </summary>
internal sealed class NhAssistantMcpConnectionPlanner(
    NhAssistantMcpHostGuard guard,
    NhAssistantMcpClientCertificates certificates,
    IEnumerable<INhAssistantMcpCredentialProvider> providers)
{
    public const string DefaultApiKeyHeader = NhAssistantMcpStaticCredentialProvider.DefaultApiKeyHeader;

    private readonly INhAssistantMcpCredentialProvider[] _providers = providers.ToArray();

    /// <summary>
    /// Plans a connection for a person's request: an administrator's test or sync, or a turn.
    /// </summary>
    public Task<TaskResult<NhAssistantMcpConnectionPlan>> PlanAsync(
        AssistantMcpServer server,
        CancellationToken cancellationToken)
    {
        return PlanAsync(server, NhAssistantMcpCredentialSubject.Request, cancellationToken);
    }

    public async Task<TaskResult<NhAssistantMcpConnectionPlan>> PlanAsync(
        AssistantMcpServer server,
        NhAssistantMcpCredentialSubject subject,
        CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(server.Url, UriKind.Absolute, out var endpoint))
        {
            return Failed(NhAssistantAdminErrorCodes.McpHostBlocked);
        }
        var blocked = guard.CheckUri(endpoint);
        if (blocked is not null)
        {
            return Failed(blocked);
        }
        try
        {
            if ((await guard.ResolveAllowedAsync(endpoint.IdnHost, cancellationToken)).Count == 0)
            {
                return Failed(NhAssistantAdminErrorCodes.McpHostBlocked);
            }
        }
        catch (SocketException)
        {
            return Failed(NhAssistantAdminErrorCodes.McpUnreachable);
        }

        X509Certificate2? certificate = null;
        if (server.ClientCertificateReference is { } reference)
        {
            var loaded = certificates.Load(reference);
            if (!loaded.Success)
            {
                return Failed(NhAssistantAdminErrorCodes.McpCertificateReferenceInvalid);
            }
            certificate = loaded.Data;
        }

        // The last registered provider of the mode wins, so a host or test can replace a built-in.
        var provider = _providers.LastOrDefault(candidate => candidate.Supports(server.AuthMode));
        if (provider is null)
        {
            return Failed(NhAssistantAdminErrorCodes.McpUnauthorized);
        }
        var credentials = new NhAssistantMcpCredentialSource(provider, server, subject);
        var credential = await credentials.GetAsync(cancellationToken);
        if (!credential.Success)
        {
            return Failed(NhAssistantMcpCredentialResults.FirstCode(credential) ?? NhAssistantAdminErrorCodes.McpUnauthorized);
        }
        // The connection's first request reuses it instead of obtaining another.
        credentials.Prime(credential.Data!);
        return TaskResult<NhAssistantMcpConnectionPlan>.Succeeded(new NhAssistantMcpConnectionPlan(
            server.Id,
            endpoint,
            credential.Data!.Headers,
            !NhAssistantMcpAuthModes.IsOrganizationCredential(server.AuthMode))
        {
            Credentials = credentials,
            Transport = server.Transport,
            ClientCertificate = certificate
        });
    }

    private static TaskResult<NhAssistantMcpConnectionPlan> Failed(string code)
    {
        return TaskResult<NhAssistantMcpConnectionPlan>.Failed(code, "The MCP server cannot be contacted with this configuration.");
    }
}

/// <summary>
/// Opens an MCP client for a connection plan.
/// </summary>
internal interface INhAssistantMcpClientFactory
{
    Task<McpClient> ConnectAsync(NhAssistantMcpConnectionPlan plan, CancellationToken cancellationToken);
}

/// <summary>
/// HTTP client: Streamable HTTP, or with <see cref="NhAssistantMcpTransports.Auto"/> the SDK's fallback
/// to the deprecated HTTP+SSE transport. Every connection re-checks the resolved address, so DNS
/// rebinding cannot reach a blocked range, and every request passes
/// <see cref="NhAssistantMcpCredentialHandler"/>, which keeps it on the configured origin (also the
/// SSE <c>endpoint</c> event) and attaches the current credential. A configured client certificate
/// is presented for mutual TLS. SDK logging is disabled because it can include request headers, and
/// the client opens no standalone stream for server-initiated messages, which the assistant does not
/// handle.
/// </summary>
internal sealed class NhAssistantHttpMcpClientFactory(
    NhAssistantMcpHostGuard guard,
    IOptionsMonitor<NhAssistantOptions> options) : INhAssistantMcpClientFactory
{
    public async Task<McpClient> ConnectAsync(NhAssistantMcpConnectionPlan plan, CancellationToken cancellationToken)
    {
        var handler = CreateSocketsHandler(plan);
        var timeout = options.CurrentValue.Mcp.ConnectTimeout;
        HttpMessageHandler pipeline = handler;
        IDictionary<string, string>? staticHeaders = null;
        if (plan.Credentials is { } credentials)
        {
            pipeline = new NhAssistantMcpCredentialHandler(credentials, plan.Endpoint, guard) { InnerHandler = handler };
        }
        else
        {
            staticHeaders = new Dictionary<string, string>(plan.Headers);
        }
        var httpClient = new HttpClient(pipeline, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions
            {
                Endpoint = plan.Endpoint,
                TransportMode = plan.Transport == NhAssistantMcpTransports.Auto
                    ? HttpTransportMode.AutoDetect
                    : HttpTransportMode.StreamableHttp,
                AdditionalHeaders = staticHeaders,
                EnableStandaloneGetStream = false,
                ConnectionTimeout = timeout,
                Name = "newheap-assistant-" + plan.ServerId
            },
            httpClient,
            NullLoggerFactory.Instance,
            ownsHttpClient: true);
        using var connect = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        connect.CancelAfter(timeout);
        return await McpClient.CreateAsync(transport, new McpClientOptions(), NullLoggerFactory.Instance, connect.Token);
    }

    /// <summary>
    /// The socket handler of a connection: no redirects, the address check at connect time and the
    /// client certificate of the plan.
    /// </summary>
    internal SocketsHttpHandler CreateSocketsHandler(NhAssistantMcpConnectionPlan plan)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            ConnectCallback = async (context, token) =>
            {
                var addresses = await guard.ResolveAllowedAsync(context.DnsEndPoint.Host, token);
                if (addresses.Count == 0)
                {
                    throw new NhAssistantMcpHostBlockedException();
                }
                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    await socket.ConnectAsync(addresses.ToArray(), context.DnsEndPoint.Port, token);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            }
        };
        if (plan.ClientCertificate is { } certificate)
        {
            handler.SslOptions.ClientCertificates = new X509CertificateCollection { certificate };
        }
        return handler;
    }
}

internal sealed class NhAssistantMcpHostBlockedException : NhAssistantMcpException
{
    public NhAssistantMcpHostBlockedException()
        : base(NhAssistantAdminErrorCodes.McpHostBlocked, "The MCP server address is blocked.")
    {
    }
}

/// <summary>
/// An open client and its tool list.
/// </summary>
internal sealed class NhAssistantMcpLease(McpClient client, IList<McpClientTool> tools, bool ownedByCaller) : IAsyncDisposable
{
    public McpClient Client { get; } = client;

    public IList<McpClientTool> Tools { get; } = tools;

    public ValueTask DisposeAsync()
    {
        return ownedByCaller ? Client.DisposeAsync() : ValueTask.CompletedTask;
    }
}

/// <summary>
/// Connects to servers and caches connections with shared credentials for
/// <see cref="NhAssistantMcpOptions.ToolListCacheDuration"/>. Connections that forward the caller's
/// token are never shared.
/// </summary>
internal sealed class NhAssistantMcpConnectionCache(
    INhAssistantMcpClientFactory factory,
    IOptionsMonitor<NhAssistantOptions> options) : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, CacheEntry> _entries = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<TaskResult<NhAssistantMcpLease>> ConnectAsync(
        AssistantMcpServer server,
        NhAssistantMcpConnectionPlan plan,
        bool bypassCache,
        CancellationToken cancellationToken)
    {
        var key = server.Id + "@" + server.UpdatedAt.UtcTicks;
        if (!plan.IsPerCaller && !bypassCache
            && _entries.TryGetValue(key, out var cached) && cached.ExpiresAt > DateTimeOffset.UtcNow)
        {
            return TaskResult<NhAssistantMcpLease>.Succeeded(new NhAssistantMcpLease(cached.Client, cached.Tools, false));
        }

        McpClient? client = null;
        try
        {
            client = await factory.ConnectAsync(plan, cancellationToken);
            var tools = await client.ListToolsAsync(cancellationToken: cancellationToken);
            if (plan.IsPerCaller)
            {
                return TaskResult<NhAssistantMcpLease>.Succeeded(new NhAssistantMcpLease(client, tools, true));
            }

            await _gate.WaitAsync(cancellationToken);
            try
            {
                foreach (var stale in _entries.Where(entry => entry.Key.StartsWith(server.Id + "@", StringComparison.Ordinal)).ToArray())
                {
                    if (_entries.TryRemove(stale.Key, out var removed))
                    {
                        await removed.Client.DisposeAsync();
                    }
                }
                _entries[key] = new CacheEntry(client, tools, DateTimeOffset.UtcNow.Add(options.CurrentValue.Mcp.ToolListCacheDuration));
            }
            finally
            {
                _gate.Release();
            }
            return TaskResult<NhAssistantMcpLease>.Succeeded(new NhAssistantMcpLease(client, tools, false));
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            if (client is not null)
            {
                await client.DisposeAsync();
            }
            return TaskResult<NhAssistantMcpLease>.Failed(Classify(exception), "The MCP server could not be reached.");
        }
    }

    public async Task InvalidateAsync(string serverId)
    {
        foreach (var entry in _entries.Where(item => item.Key.StartsWith(serverId + "@", StringComparison.Ordinal)).ToArray())
        {
            if (_entries.TryRemove(entry.Key, out var removed))
            {
                await removed.Client.DisposeAsync();
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var entry in _entries.Values)
        {
            await entry.Client.DisposeAsync();
        }
        _entries.Clear();
    }

    /// <summary>
    /// Maps a connection failure to a stable code without exposing its message.
    /// </summary>
    public static string Classify(Exception exception)
    {
        return NhAssistantMcpFailures.Classify(exception);
    }

    private sealed record CacheEntry(McpClient Client, IList<McpClientTool> Tools, DateTimeOffset ExpiresAt);
}

/// <summary>
/// Imports the enabled tools of the MCP servers assigned to an agent through
/// <see cref="INhAiMcpClientToolImporter"/>, so every call runs through the shared invoker.
/// Servers the caller may not use, disabled servers, unreachable servers and, in headless turns,
/// servers that do not allow headless use are skipped with a stable code.
/// </summary>
internal sealed partial class NhAssistantMcpToolSource(
    NhAssistantAdminStore store,
    NhAssistantMcpConnectionPlanner planner,
    NhAssistantMcpConnectionCache cache,
    INhAiMcpClientToolImporter importer,
    NhAssistantMcpContextBindings contextBindings,
    NhAssistantRegistrationState state,
    IOptionsMonitor<NhAssistantOptions> options,
    IHttpContextAccessor httpContextAccessor,
    IAuthorizationService authorizationService,
    IServiceProvider services,
    ILogger<NhAssistantMcpToolSource> logger) : INhAssistantMcpToolSource
{
    public const int MaxToolDescriptionLength = 512;

    /// <summary>
    /// How long a successful connection with the shared credential is not recorded again.
    /// </summary>
    public static readonly TimeSpan HealthRecordInterval = TimeSpan.FromMinutes(5);

    public async Task<NhAssistantMcpToolSet> GetToolsAsync(NhAssistantMcpToolRequest request, CancellationToken cancellationToken)
    {
        var functions = new List<AIFunction>();
        var leases = new List<NhAssistantMcpLease>();
        var skipped = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var serverId in request.Agent.McpServerIds)
        {
            var server = await store.FindMcpServerAsync(serverId, cancellationToken);
            if (server is null || !server.IsEnabled)
            {
                continue;
            }
            if (request.IsHeadless && await HeadlessRefusalAsync(server, request, cancellationToken) is { } refusal)
            {
                skipped[serverId] = refusal;
                continue;
            }
            if (!await CanUseAsync(server, request, cancellationToken))
            {
                skipped[serverId] = NhAssistantAdminErrorCodes.Forbidden;
                continue;
            }
            var enabled = await store.ListEnabledMcpToolsAsync([serverId], cancellationToken);
            if (enabled.Count == 0)
            {
                continue;
            }
            var plan = await planner.PlanAsync(server, SubjectOf(request), cancellationToken);
            if (!plan.Success)
            {
                await SkipAsync(server, skipped, FirstCode(plan), perCaller: !NhAssistantMcpAuthModes.IsOrganizationCredential(server.AuthMode));
                continue;
            }
            var lease = await cache.ConnectAsync(server, plan.Data, bypassCache: false, cancellationToken);
            if (!lease.Success)
            {
                await SkipAsync(server, skipped, FirstCode(lease), plan.Data.IsPerCaller);
                continue;
            }
            if (!plan.Data.IsPerCaller)
            {
                await RecordHealthAsync(server, null);
            }
            leases.Add(lease.Data);
            functions.AddRange(await ImportAsync(server, plan.Data, enabled, lease.Data.Tools, cancellationToken));
        }

        return new NhAssistantMcpToolSet(functions, async () =>
        {
            foreach (var lease in leases)
            {
                await lease.DisposeAsync();
            }
            await services.GetRequiredService<NhAssistantMcpDelegatedTokenLedger>().FlushAsync(CancellationToken.None);
        })
        {
            SkippedServers = skipped
        };
    }

    /// <summary>
    /// Whether the server's tools may serve a turn without a person's request with the server's
    /// organization credential, because an administrator allowed it. Such tools accept agent grants.
    /// </summary>
    public static bool AllowsHeadlessUse(AssistantMcpServer server)
    {
        return server.HeadlessUse == NhAssistantMcpHeadlessUses.Organization
            && NhAssistantMcpAuthModes.IsOrganizationCredential(server.AuthMode);
    }

    /// <summary>
    /// Why the server's tools are not offered in a headless turn, or <see langword="null"/> when they
    /// are: with the organization credential when an administrator allowed it, or with the person's
    /// credential when an administrator allowed that and the person the run acts for consented to
    /// background use of the server as it is configured now.
    /// </summary>
    private async Task<string?> HeadlessRefusalAsync(
        AssistantMcpServer server,
        NhAssistantMcpToolRequest request,
        CancellationToken cancellationToken)
    {
        if (AllowsHeadlessUse(server))
        {
            return null;
        }
        if (server.HeadlessUse != NhAssistantMcpHeadlessUses.Personal
            || !NhAssistantMcpHeadlessUses.FitsAuthMode(server.HeadlessUse, server.AuthMode)
            || request.PersonActorId is not { } person)
        {
            return NhAssistantAdminErrorCodes.McpHeadlessNotAllowed;
        }
        var connection = await store.FindMcpConnectionAsync(
            server.Id,
            NhAssistantMcpConnectionOwners.User,
            person,
            string.Empty,
            cancellationToken);
        return NhAssistantMcpConsent.Allows(connection, server) ? null : NhAssistantAdminErrorCodes.McpConsentRequired;
    }

    private static NhAssistantMcpCredentialSubject SubjectOf(NhAssistantMcpToolRequest request)
    {
        return request.IsHeadless
            ? new NhAssistantMcpCredentialSubject(true, request.PersonActorId, request.PersonGrantSource, request.Subject?.TenantId)
            : NhAssistantMcpCredentialSubject.Request;
    }

    public static string LocalName(string remoteName)
    {
        var kebab = NonSegment().Replace(remoteName.ToLowerInvariant(), "-").Trim('-');
        kebab = RepeatedDash().Replace(kebab, "-");
        if (kebab.Length > 64)
        {
            kebab = kebab[..64].TrimEnd('-');
        }
        return kebab.Length == 0 ? "tool" : kebab;
    }

    public static string LocalId(string serverId, string remoteName)
    {
        return $"mcp.{serverId}.{LocalName(remoteName)}";
    }

    public static string SchemaHash(McpClientTool tool)
    {
        return NhAiCanonicalJson.ComputeHash(tool.JsonSchema);
    }

    private async Task<IReadOnlyList<AIFunction>> ImportAsync(
        AssistantMcpServer server,
        NhAssistantMcpConnectionPlan plan,
        IReadOnlyList<AssistantMcpTool> enabled,
        IList<McpClientTool> remoteTools,
        CancellationToken cancellationToken)
    {
        var policies = new List<NhAiMcpImportedToolPolicy>();
        var remotes = new List<McpClientTool>();
        var localNames = new HashSet<string>(StringComparer.Ordinal);
        var allowAgentGrant = AllowsHeadlessUse(server);
        foreach (var tool in enabled)
        {
            var remote = remoteTools.FirstOrDefault(item => string.Equals(item.Name, tool.RemoteName, StringComparison.Ordinal));
            if (remote is null)
            {
                // A list seen with one caller's credential never changes the reviewed catalog.
                if (plan.IsPerCaller)
                {
                    LogToolSkipped(server.Id, NhAssistantMcpToolStatuses.Missing);
                    continue;
                }
                await store.UpdateMcpToolAsync(server.Id, tool.RemoteName, item =>
                {
                    item.IsEnabled = false;
                    item.Status = NhAssistantMcpToolStatuses.Missing;
                }, cancellationToken);
                continue;
            }
            if (!string.Equals(SchemaHash(remote), tool.InputSchemaHash, StringComparison.Ordinal))
            {
                if (plan.IsPerCaller)
                {
                    LogToolSkipped(server.Id, NhAssistantAdminErrorCodes.McpSchemaChanged);
                    continue;
                }
                // A changed remote contract disables the tool until an administrator re-activates it.
                await store.UpdateMcpToolAsync(server.Id, tool.RemoteName, item =>
                {
                    item.IsEnabled = false;
                    item.Status = NhAssistantMcpToolStatuses.SchemaChanged;
                }, cancellationToken);
                LogSkipped(server.Id, NhAssistantAdminErrorCodes.McpSchemaChanged);
                continue;
            }
            var localName = LocalName(tool.RemoteName);
            if (!localNames.Add(localName))
            {
                continue;
            }
            var readOnly = tool.Effect == NhAssistantMcpToolEffects.ReadOnly;
            var description = string.IsNullOrWhiteSpace(tool.DescriptionOverride) ? tool.Description : tool.DescriptionOverride;
            if (string.IsNullOrWhiteSpace(description))
            {
                description = tool.RemoteName;
            }
            policies.Add(new NhAiMcpImportedToolPolicy(
                tool.RemoteName,
                localName,
                description.Length > MaxToolDescriptionLength ? description[..MaxToolDescriptionLength] : description,
                readOnly ? NhAiToolEffect.ReadOnly : NhAiToolEffect.Mutation,
                NhAiToolExposure.Agent,
                [server.RequiredPolicy ?? AccessPolicy()])
            {
                Approval = readOnly ? NhAiApprovalRequirement.PolicyControlled : NhAiApprovalRequirement.Required,
                Idempotency = readOnly ? NhAiIdempotencySupport.None : NhAiIdempotencySupport.Required,
                DataClassification = NhAiDataClassification.Internal,
                // Only a host-reviewed server, URL and tool receives the assistant context.
                InvocationBinderId = contextBindings.Find(server, tool.RemoteName) is null ? null : NhAssistantMcpContextBinder.BinderId,
                // An organization credential that an administrator allowed in headless turns may serve
                // an agent through a code grant; a person's credential never does.
                AllowAgentGrant = allowAgentGrant
            });
            remotes.Add(remote);
        }
        if (policies.Count == 0)
        {
            return [];
        }
        var catalog = importer.Import(remotes, new NhAiMcpImportOptions(server.Id, server.Id, policies));
        return catalog.CreateFunctions(services);
    }

    /// <summary>
    /// Whether the turn's person may use the server: the requesting person in a person's turn, the
    /// person whose rights a headless turn uses otherwise. A headless turn of an agent that acts for
    /// itself may only use servers without a required policy.
    /// </summary>
    private async Task<bool> CanUseAsync(
        AssistantMcpServer server,
        NhAssistantMcpToolRequest request,
        CancellationToken cancellationToken)
    {
        if (server.RequiredPolicy is null)
        {
            return true;
        }

        ClaimsPrincipal? user;
        if (!request.IsHeadless)
        {
            user = httpContextAccessor.HttpContext?.User;
        }
        else if (request.PersonActorId is null || request.Subject is null)
        {
            return false;
        }
        else
        {
            var principals = services.GetService<INhAiPrincipalFactory>();
            user = principals is null
                ? null
                : await principals.CreateAsync(request.PersonActorId, request.Subject, cancellationToken);
        }
        return user is not null && (await authorizationService.AuthorizeAsync(user, server.RequiredPolicy)).Succeeded;
    }

    private async Task SkipAsync(
        AssistantMcpServer server,
        Dictionary<string, string> skipped,
        string? code,
        bool perCaller)
    {
        var failure = code ?? NhAssistantAdminErrorCodes.McpUnreachable;
        skipped[server.Id] = failure;
        LogSkipped(server.Id, failure);
        if (!perCaller)
        {
            await RecordHealthAsync(server, failure);
        }
    }

    /// <summary>
    /// Records the outcome of a connection with the shared credential when it changed, or when a
    /// success was last recorded more than <see cref="HealthRecordInterval"/> ago. A failure to
    /// record never fails the turn.
    /// </summary>
    private async Task RecordHealthAsync(AssistantMcpServer server, string? code)
    {
        var now = DateTimeOffset.UtcNow;
        if (string.Equals(server.LastConnectCode, code, StringComparison.Ordinal)
            && server.LastConnectAt is { } last
            && now - last < HealthRecordInterval)
        {
            return;
        }
        try
        {
            await store.RecordMcpConnectAsync(server.Id, now, code, CancellationToken.None);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogHealthNotRecorded(server.Id, exception.GetType().Name);
        }
    }

    private string AccessPolicy()
    {
        return NhAssistantEndpointOptions.ResolveAccessPolicy(state, options.CurrentValue);
    }

    private void LogSkipped(string serverId, string? code)
    {
        logger.LogWarning("Assistant MCP server {ServerId} was skipped for this turn ({Code}).", serverId, code ?? "unclassified");
    }

    private void LogToolSkipped(string serverId, string code)
    {
        logger.LogInformation("A tool of assistant MCP server {ServerId} was skipped for this caller ({Code}).", serverId, code);
    }

    private void LogHealthNotRecorded(string serverId, string exceptionType)
    {
        logger.LogWarning("The connection state of assistant MCP server {ServerId} was not recorded ({ExceptionType}).", serverId, exceptionType);
    }

    private static string? FirstCode(TaskResult result)
    {
        return result.GetResultItems().Select(item => item.Name).FirstOrDefault(name => !string.IsNullOrEmpty(name));
    }

    [GeneratedRegex("[^a-z0-9]+", RegexOptions.CultureInvariant)]
    private static partial Regex NonSegment();

    [GeneratedRegex("-{2,}", RegexOptions.CultureInvariant)]
    private static partial Regex RepeatedDash();
}
