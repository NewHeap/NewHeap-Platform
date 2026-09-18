using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
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
/// A validated connection target: endpoint and the headers to send.
/// </summary>
internal sealed record NhAssistantMcpConnectionPlan(
    string ServerId,
    Uri Endpoint,
    IReadOnlyDictionary<string, string> Headers,
    bool IsPerCaller);

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
/// Builds the connection plan of a server: SSRF checks and authentication headers.
/// </summary>
internal sealed class NhAssistantMcpConnectionPlanner(
    NhAssistantMcpHostGuard guard,
    IOptionsMonitor<NhAssistantOptions> options,
    NhAssistantMcpSecretProtector secrets,
    IServiceProvider services)
{
    public const string DefaultApiKeyHeader = "X-Api-Key";

    public async Task<TaskResult<NhAssistantMcpConnectionPlan>> PlanAsync(
        AssistantMcpServer server,
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

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        switch (server.AuthMode)
        {
            case NhAssistantMcpAuthModes.Bearer:
            {
                var secret = secrets.Unprotect(server.ProtectedSecret);
                if (secret is null)
                {
                    return Failed(NhAssistantAdminErrorCodes.McpUnauthorized);
                }
                headers["Authorization"] = "Bearer " + secret;
                break;
            }
            case NhAssistantMcpAuthModes.ApiKeyHeader:
            {
                var secret = secrets.Unprotect(server.ProtectedSecret);
                if (secret is null)
                {
                    return Failed(NhAssistantAdminErrorCodes.McpUnauthorized);
                }
                headers[string.IsNullOrWhiteSpace(server.HeaderName) ? DefaultApiKeyHeader : server.HeaderName] = secret;
                break;
            }
            case NhAssistantMcpAuthModes.ForwardUserToken:
            {
                var host = endpoint.IdnHost.TrimEnd('.').ToLowerInvariant();
                if (!options.CurrentValue.Mcp.ForwardUserTokenHosts.Any(allowed =>
                    string.Equals(allowed.Trim().TrimEnd('.'), host, StringComparison.OrdinalIgnoreCase)))
                {
                    return Failed(NhAssistantAdminErrorCodes.McpHostBlocked);
                }
                var accessor = services.GetService<INhAiCallerCredentialAccessor>();
                var token = accessor is null ? null : await accessor.GetBearerTokenAsync(cancellationToken);
                if (string.IsNullOrWhiteSpace(token))
                {
                    return Failed(NhAssistantAdminErrorCodes.McpUnauthorized);
                }
                headers["Authorization"] = "Bearer " + token;
                break;
            }
        }
        return TaskResult<NhAssistantMcpConnectionPlan>.Succeeded(new NhAssistantMcpConnectionPlan(
            server.Id,
            endpoint,
            headers,
            server.AuthMode == NhAssistantMcpAuthModes.ForwardUserToken));
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
/// Streamable HTTP client. Every connection re-checks the resolved address, so DNS rebinding
/// cannot reach a blocked range. SDK logging is disabled because it can include request headers.
/// </summary>
internal sealed class NhAssistantHttpMcpClientFactory(
    NhAssistantMcpHostGuard guard,
    IOptionsMonitor<NhAssistantOptions> options) : INhAssistantMcpClientFactory
{
    public async Task<McpClient> ConnectAsync(NhAssistantMcpConnectionPlan plan, CancellationToken cancellationToken)
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
        var timeout = options.CurrentValue.Mcp.ConnectTimeout;
        var httpClient = new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions
            {
                Endpoint = plan.Endpoint,
                TransportMode = HttpTransportMode.StreamableHttp,
                AdditionalHeaders = new Dictionary<string, string>(plan.Headers),
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
}

internal sealed class NhAssistantMcpHostBlockedException : Exception
{
    public NhAssistantMcpHostBlockedException()
        : base("The MCP server address is blocked.")
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
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is NhAssistantMcpHostBlockedException)
            {
                return NhAssistantAdminErrorCodes.McpHostBlocked;
            }
            if (current is HttpRequestException { StatusCode: HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden })
            {
                return NhAssistantAdminErrorCodes.McpUnauthorized;
            }
        }
        return NhAssistantAdminErrorCodes.McpUnreachable;
    }

    private sealed record CacheEntry(McpClient Client, IList<McpClientTool> Tools, DateTimeOffset ExpiresAt);
}

/// <summary>
/// Imports the enabled tools of the MCP servers assigned to an agent through
/// <see cref="INhAiMcpClientToolImporter"/>, so every call runs through the shared invoker.
/// Servers the caller may not use, disabled servers and unreachable servers are skipped.
/// </summary>
internal sealed partial class NhAssistantMcpToolSource(
    NhAssistantAdminStore store,
    NhAssistantMcpConnectionPlanner planner,
    NhAssistantMcpConnectionCache cache,
    INhAiMcpClientToolImporter importer,
    NhAssistantRegistrationState state,
    IOptionsMonitor<NhAssistantOptions> options,
    IHttpContextAccessor httpContextAccessor,
    IAuthorizationService authorizationService,
    IServiceProvider services,
    ILogger<NhAssistantMcpToolSource> logger) : INhAssistantMcpToolSource
{
    public const int MaxToolDescriptionLength = 512;

    public async Task<NhAssistantMcpToolSet> GetToolsAsync(NhAssistantAgent agent, CancellationToken cancellationToken)
    {
        var functions = new List<AIFunction>();
        var leases = new List<NhAssistantMcpLease>();
        foreach (var serverId in agent.McpServerIds)
        {
            var server = await store.FindMcpServerAsync(serverId, cancellationToken);
            if (server is null || !server.IsEnabled || !await CanUseAsync(server))
            {
                continue;
            }
            var enabled = await store.ListEnabledMcpToolsAsync([serverId], cancellationToken);
            if (enabled.Count == 0)
            {
                continue;
            }
            var plan = await planner.PlanAsync(server, cancellationToken);
            if (!plan.Success)
            {
                LogSkipped(serverId, FirstCode(plan));
                continue;
            }
            var lease = await cache.ConnectAsync(server, plan.Data, bypassCache: false, cancellationToken);
            if (!lease.Success)
            {
                LogSkipped(serverId, FirstCode(lease));
                continue;
            }
            leases.Add(lease.Data);
            functions.AddRange(await ImportAsync(server, enabled, lease.Data.Tools, cancellationToken));
        }

        return new NhAssistantMcpToolSet(functions, async () =>
        {
            foreach (var lease in leases)
            {
                await lease.DisposeAsync();
            }
        });
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
        IReadOnlyList<AssistantMcpTool> enabled,
        IList<McpClientTool> remoteTools,
        CancellationToken cancellationToken)
    {
        var policies = new List<NhAiMcpImportedToolPolicy>();
        var remotes = new List<McpClientTool>();
        var localNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var tool in enabled)
        {
            var remote = remoteTools.FirstOrDefault(item => string.Equals(item.Name, tool.RemoteName, StringComparison.Ordinal));
            if (remote is null)
            {
                await store.UpdateMcpToolAsync(server.Id, tool.RemoteName, item =>
                {
                    item.IsEnabled = false;
                    item.Status = NhAssistantMcpToolStatuses.Missing;
                }, cancellationToken);
                continue;
            }
            if (!string.Equals(SchemaHash(remote), tool.InputSchemaHash, StringComparison.Ordinal))
            {
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
                DataClassification = NhAiDataClassification.Internal
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

    private async Task<bool> CanUseAsync(AssistantMcpServer server)
    {
        if (server.RequiredPolicy is null)
        {
            return true;
        }
        var user = httpContextAccessor.HttpContext?.User;
        return user is not null && (await authorizationService.AuthorizeAsync(user, server.RequiredPolicy)).Succeeded;
    }

    private string AccessPolicy()
    {
        return NhAssistantEndpointOptions.ResolveAccessPolicy(state, options.CurrentValue);
    }

    private void LogSkipped(string serverId, string? code)
    {
        logger.LogWarning("Assistant MCP server {ServerId} was skipped for this turn ({Code}).", serverId, code ?? "unclassified");
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
