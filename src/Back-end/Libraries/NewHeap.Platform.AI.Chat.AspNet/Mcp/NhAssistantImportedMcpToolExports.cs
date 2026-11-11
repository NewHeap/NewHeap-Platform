using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using NewHeap.Platform.AI.Chat.AspNet.Mcp;
using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.AI.Chat.Runtime;
using NewHeap.Platform.AI.Mcp;

namespace NewHeap.Platform.AI.Chat.AspNet;

/// <summary>
/// A reviewed tool of an MCP server the assistant imports, as a host's
/// <see cref="INhAssistantImportedMcpToolExportSelector"/> sees it: the server, the tool's remote name
/// and the description a turn offers, which an administrator reviewed. The remote server's own text never
/// decides anything; it is data.
/// </summary>
public sealed record NhAssistantExportableMcpTool(
    string ServerId,
    string ServerDisplayName,
    string ServerUrl,
    string AuthMode,
    string HeadlessUse,
    string RemoteName,
    string Description);

/// <summary>
/// The host's decision to export one imported tool to an MCP client, under
/// <paramref name="ExportName"/>: 1 to <see cref="NhAssistantImportedMcpToolExports.MaxExportNameLength"/>
/// letters, digits, underscores, dots or dashes, unique in the export.
/// </summary>
public sealed record NhAssistantImportedMcpToolExport(string ExportName)
{
    /// <summary>
    /// Exports the tool although its server serves background runs with the organization's credential
    /// (<see cref="NhAssistantMcpHeadlessUses.Organization"/>). Such a server acts for nobody in
    /// particular, so every caller would use that credential's rights; set it only for a server that
    /// binds each call to the person itself.
    /// </summary>
    public bool AllowOrganizationHeadlessServer { get; init; }
}

/// <summary>
/// Decides which reviewed imported MCP tools the host's MCP export offers to a caller, and under which
/// name. Called per request for each enabled, read-only tool of an enabled server with the organization's
/// credential that the caller may use; <see langword="null"/> leaves the tool out. It is a scoped service.
/// </summary>
public interface INhAssistantImportedMcpToolExportSelector
{
    ValueTask<NhAssistantImportedMcpToolExport?> SelectAsync(
        NhAssistantExportableMcpTool tool,
        NhAiInvocationContext caller,
        CancellationToken cancellationToken);
}

/// <summary>
/// Opt-in export of reviewed imported MCP tools to the host's own MCP clients, such as a person's AI app,
/// next to the tools of <c>WithNewHeapPlatformAITools</c>. Without it imported tools never reach an MCP
/// client.
/// </summary>
public static class NhAssistantImportedMcpToolExports
{
    /// <summary>The longest export name.</summary>
    public const int MaxExportNameLength = 64;

    /// <summary>
    /// Offers the imported MCP tools that <typeparamref name="TSelector"/> selects to the callers of the
    /// host's MCP export. An exported tool runs through <see cref="INhAiToolInvoker"/> exactly as in a
    /// person's turn, with the server's own credential and as the calling person, and is audited as
    /// such; the caller's own token is never passed on. Only read-only tools are exported; only servers
    /// whose credential belongs to the organization; not a server that serves background runs with that
    /// credential unless the selection allows it; and only when the caller passes the server's required
    /// policy and the host's <see cref="INhAiToolDiscoveryPolicy"/>. A reviewed tool whose schema changed
    /// is left out. An export name another tool of the export has fails the listing. Needs
    /// <c>WithNewHeapPlatformAITools</c>; a later call replaces the selector.
    /// </summary>
    public static NhAssistantBuilder ExportImportedMcpTools<TSelector>(this NhAssistantBuilder builder)
        where TSelector : class, INhAssistantImportedMcpToolExportSelector
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.Replace(ServiceDescriptor.Scoped<INhAssistantImportedMcpToolExportSelector, TSelector>());
        builder.Services.AddNewHeapPlatformAIMcpToolContributor<NhAssistantImportedMcpToolExporter>();
        return builder;
    }

    /// <summary>
    /// Whether <paramref name="name"/> can be an export name: 1 to <see cref="MaxExportNameLength"/>
    /// letters, digits, underscores, dots or dashes.
    /// </summary>
    public static bool IsExportName(string? name)
    {
        return !string.IsNullOrEmpty(name) && name.Length <= MaxExportNameLength && ExportNamePattern.IsMatch(name);
    }

    private static readonly Regex ExportNamePattern = new("^[A-Za-z0-9_.-]+$", RegexOptions.CultureInvariant);
}

/// <summary>
/// The exported call in progress: the person the export authorized, so the assistant context binder can
/// bind the call without a turn. Set only by an exported tool around its own invocation.
/// </summary>
internal static class NhAssistantMcpExportCall
{
    /// <summary>The <c>channel</c> of the assistant context of an exported call.</summary>
    public const string Channel = "mcp";

    private static readonly AsyncLocal<string?> Current = new();

    public static string? ActorId => Current.Value;

    public static IDisposable Enter(string actorId)
    {
        var previous = Current.Value;
        Current.Value = actorId;
        return new Scope(previous);
    }

    private sealed class Scope(string? previous) : IDisposable
    {
        public void Dispose()
        {
            Current.Value = previous;
        }
    }
}

/// <summary>
/// Adds the imported tools the host's selector chooses to the MCP export of one request. The tools are
/// imported for the caller through <see cref="NhAssistantMcpToolSource"/>, as in the caller's own turn;
/// their connections close with the request.
/// </summary>
internal sealed partial class NhAssistantImportedMcpToolExporter(
    NhAssistantAdminStore store,
    NhAssistantMcpToolSource source,
    NhAssistantMcpAuthModeCatalog modes,
    INhAssistantImportedMcpToolExportSelector selector,
    INhAiToolDiscoveryPolicy discovery,
    INhAiCapabilityResolver capabilities,
    IHttpContextAccessor httpContextAccessor,
    IAuthorizationService authorizationService,
    IOptionsMonitor<NhAssistantOptions> options,
    ILogger<NhAssistantImportedMcpToolExporter> logger) : INhAiMcpToolContributor, IAsyncDisposable
{
    private readonly List<NhAssistantMcpToolSet> _sets = [];

    public async ValueTask<IReadOnlyList<McpServerTool>> CreateToolsAsync(
        NhAiMcpToolContribution contribution,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(contribution);
        var caller = contribution.Context;
        var user = httpContextAccessor.HttpContext?.User;
        // A switched-off assistant exports nothing; a person calls an exported tool for themselves, an
        // agent or service never does.
        if (!options.CurrentValue.Enabled
            || caller.ActorKind != NhAiActorKind.Human || string.IsNullOrWhiteSpace(caller.ActorId) || user is null)
        {
            return [];
        }

        var selected = new List<(AssistantMcpServer Server, IReadOnlyList<AssistantMcpTool> Tools)>();
        var exportNames = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (server, _) in await store.ListMcpServersAsync(cancellationToken))
        {
            // A person's own connection, or a token forwarded from the caller, never serves the export.
            if (!server.IsEnabled || !modes.IsOrganizationCredential(server.AuthMode, server.ConnectionScope))
            {
                continue;
            }
            var policy = server.RequiredPolicy ?? source.AccessPolicyName;
            if (!(await authorizationService.AuthorizeAsync(user, policy)).Succeeded)
            {
                continue;
            }

            var chosen = new List<AssistantMcpTool>();
            foreach (var tool in await store.ListEnabledMcpToolsAsync([server.Id], cancellationToken))
            {
                if (tool.Effect != NhAssistantMcpToolEffects.ReadOnly)
                {
                    continue;
                }
                var export = await selector.SelectAsync(Describe(server, tool), caller, cancellationToken);
                if (export is null)
                {
                    continue;
                }
                if (!NhAssistantImportedMcpToolExports.IsExportName(export.ExportName))
                {
                    throw new InvalidOperationException(
                        $"The export name of MCP tool '{tool.RemoteName}' of server '{server.Id}' needs 1 to {NhAssistantImportedMcpToolExports.MaxExportNameLength} letters, digits, underscores, dots or dashes.");
                }
                if (source.AllowsHeadlessUse(server) && !export.AllowOrganizationHeadlessServer)
                {
                    LogHeadlessServerRefused(server.Id);
                    continue;
                }
                chosen.Add(tool);
                exportNames[NhAssistantMcpToolSource.LocalId(server.Id, tool.RemoteName)] = export.ExportName;
            }
            if (chosen.Count > 0)
            {
                selected.Add((server, chosen));
            }
        }
        if (selected.Count == 0)
        {
            return [];
        }

        var set = await source.GetExportToolsAsync(selected, caller.ActorId, cancellationToken);
        _sets.Add(set);
        var tools = new List<McpServerTool>(set.Functions.Count);
        foreach (var function in set.Functions)
        {
            if (function is not INhAiGovernedAIFunction governed
                || governed.Descriptor.Effect != NhAiToolEffect.ReadOnly
                || !exportNames.TryGetValue(governed.Descriptor.Id, out var exportName))
            {
                continue;
            }
            // The host decides per caller and tool which tools its export lists, as for its own tools.
            var descriptor = governed.Descriptor;
            var resolved = await capabilities.ResolveAsync(descriptor, caller, DateTimeOffset.UtcNow, cancellationToken);
            if (!resolved.Succeeded || !await discovery.CanDiscoverAsync(descriptor, caller, cancellationToken))
            {
                continue;
            }
            tools.Add(new NhAssistantExportedMcpTool(function, descriptor, exportName, caller.ActorId));
        }
        return tools;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var set in _sets)
        {
            await set.DisposeAsync();
        }
        _sets.Clear();
    }

    private static NhAssistantExportableMcpTool Describe(AssistantMcpServer server, AssistantMcpTool tool)
    {
        return new NhAssistantExportableMcpTool(
            server.Id,
            server.DisplayName,
            server.Url,
            server.AuthMode,
            server.HeadlessUse,
            tool.RemoteName,
            NhAssistantMcpToolSource.ReviewedDescription(tool));
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "A tool of assistant MCP server {ServerId} was not exported: the server serves background runs with the organization's credential and the selection does not allow that.")]
    private partial void LogHeadlessServerRefused(string serverId);
}

/// <summary>
/// One exported imported tool: the governed function of the turn, offered under the host's export name
/// with the reviewed description and the remote input schema, read-only and open-world. Its call runs as
/// the exported call of the person the export authorized.
/// </summary>
internal sealed class NhAssistantExportedMcpTool : McpServerTool
{
    private readonly McpServerTool _inner;
    private readonly string _actorId;

    public NhAssistantExportedMcpTool(
        Microsoft.Extensions.AI.AIFunction function,
        NhAiToolDescriptor descriptor,
        string exportName,
        string actorId)
    {
        _actorId = actorId;
        _inner = new NhAiOutcomeAwareMcpServerTool(
            Create(function, new McpServerToolCreateOptions
            {
                Name = exportName,
                Description = descriptor.Description,
                UseStructuredContent = true,
                ReadOnly = true,
                Destructive = false,
                Idempotent = true,
                OpenWorld = true
            }),
            descriptor);
    }

    public override Tool ProtocolTool => _inner.ProtocolTool;

    public override IReadOnlyList<object> Metadata => _inner.Metadata;

    public override async ValueTask<CallToolResult> InvokeAsync(
        RequestContext<CallToolRequestParams> request,
        CancellationToken cancellationToken = default)
    {
        using (NhAssistantMcpExportCall.Enter(_actorId))
        {
            return await _inner.InvokeAsync(request, cancellationToken);
        }
    }
}
