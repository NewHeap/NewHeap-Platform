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
/// the description a turn offers and the effect (<see cref="NhAssistantMcpToolEffects"/>), which an
/// administrator reviewed. The remote server's own text never decides anything; it is data.
/// </summary>
public sealed record NhAssistantExportableMcpTool(
    string ServerId,
    string ServerDisplayName,
    string ServerUrl,
    string AuthMode,
    string HeadlessUse,
    string RemoteName,
    string Description,
    string Effect);

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

    /// <summary>
    /// Required to export a tool that changes something: the host declares that the person's client asks
    /// the person to confirm each call, and that this confirmation is the approval. NewHeap then records
    /// the call as consumer-authoritatively approved, offers it as destructive so clients ask, and sends
    /// the remote server the person and an operation key it can hold replays against. Without it a
    /// mutating tool is not exported; a read-only tool ignores it.
    /// </summary>
    public bool ClientConfirmationIsApproval { get; init; }
}

/// <summary>
/// Decides which reviewed imported MCP tools the host's MCP export offers to a caller, and under which
/// name. Called per request for each enabled tool of an enabled server with the organization's credential
/// that the caller may use; <see langword="null"/> leaves the tool out. It is a scoped service.
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
    /// such; the caller's own token is never passed on. Read-only tools are exported as they are; a tool
    /// that changes something only with <see cref="NhAssistantImportedMcpToolExport.ClientConfirmationIsApproval"/>
    /// and a reviewed context binding, which sends the remote server the person and an operation key
    /// (<see cref="NhAssistantMcpExportCall.IdempotencyKeyMetaKey"/> from the client, or else one per
    /// call). Only servers whose credential belongs to the organization; not a server that serves background runs with that
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
/// An exported call of an imported MCP tool: the <c>channel</c> of its assistant context and the request
/// metadata key a client names an operation with. Inside, the person the export authorized and, for a
/// change, the operation key, set only by an exported tool around its own invocation, so the assistant
/// context binder can bind the call without a turn.
/// </summary>
public static class NhAssistantMcpExportCall
{
    /// <summary>The <c>channel</c> of the assistant context of an exported call.</summary>
    public const string Channel = "mcp";

    /// <summary>
    /// The request metadata key under which an MCP client may name one operation of a mutating exported
    /// tool, so a retry carries the same key: 1 to <see cref="MaxIdempotencyKeyLength"/> letters, digits,
    /// dots, colons, underscores or dashes. Without a valid one each call gets a key of its own.
    /// </summary>
    public const string IdempotencyKeyMetaKey = "com.newheap/idempotency-key";

    /// <summary>The longest operation key a client may send.</summary>
    public const int MaxIdempotencyKeyLength = 128;

    private static readonly AsyncLocal<ExportCall?> Current = new();

    internal static string? ActorId => Current.Value?.ActorId;

    internal static string? IdempotencyKey => Current.Value?.IdempotencyKey;

    internal static IDisposable Enter(string actorId, string? idempotencyKey)
    {
        var previous = Current.Value;
        Current.Value = new ExportCall(actorId, idempotencyKey);
        return new Scope(previous);
    }

    internal static bool IsIdempotencyKey(string? key)
    {
        return !string.IsNullOrEmpty(key)
               && key.Length <= MaxIdempotencyKeyLength
               && key.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or ':' or '_' or '-');
    }

    private sealed record ExportCall(string ActorId, string? IdempotencyKey);

    private sealed class Scope(ExportCall? previous) : IDisposable
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
        var clientConfirmed = new HashSet<string>(StringComparer.Ordinal);
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
                var localId = NhAssistantMcpToolSource.LocalId(server.Id, tool.RemoteName);
                if (tool.Effect != NhAssistantMcpToolEffects.ReadOnly)
                {
                    // A change needs the host's word that the person's client confirmation approves it.
                    if (!export.ClientConfirmationIsApproval)
                    {
                        LogMutationNotConfirmed(server.Id);
                        continue;
                    }
                    clientConfirmed.Add(localId);
                }
                chosen.Add(tool);
                exportNames[localId] = export.ExportName;
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

        var set = await source.GetExportToolsAsync(selected, caller.ActorId, clientConfirmed, cancellationToken);
        _sets.Add(set);
        var tools = new List<McpServerTool>(set.Functions.Count);
        foreach (var function in set.Functions)
        {
            // Only a read, or a change that the person's client confirms, ever leaves the export.
            if (function is not INhAiGovernedAIFunction governed
                || (governed.Descriptor.Effect != NhAiToolEffect.ReadOnly
                    && !(clientConfirmed.Contains(governed.Descriptor.Id)
                         && governed.Descriptor.Approval == NhAiApprovalRequirement.ConsumerAuthoritative))
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
            NhAssistantMcpToolSource.ReviewedDescription(tool),
            tool.Effect);
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "A tool of assistant MCP server {ServerId} was not exported: the server serves background runs with the organization's credential and the selection does not allow that.")]
    private partial void LogHeadlessServerRefused(string serverId);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "A mutating tool of assistant MCP server {ServerId} was not exported: the selection does not declare the client's confirmation the approval.")]
    private partial void LogMutationNotConfirmed(string serverId);
}

/// <summary>
/// One exported imported tool: the governed function, offered under the host's export name with the
/// reviewed description and the remote input schema, open-world; a read as read-only, a change as
/// destructive so the person's client asks before each call. Its call runs as the exported call of the
/// person the export authorized, with the operation key a change sends to the remote server.
/// </summary>
internal sealed class NhAssistantExportedMcpTool : McpServerTool
{
    private readonly McpServerTool _inner;
    private readonly string _actorId;
    private readonly bool _mutation;

    public NhAssistantExportedMcpTool(
        Microsoft.Extensions.AI.AIFunction function,
        NhAiToolDescriptor descriptor,
        string exportName,
        string actorId)
    {
        _actorId = actorId;
        _mutation = descriptor.Effect != NhAiToolEffect.ReadOnly;
        _inner = new NhAiOutcomeAwareMcpServerTool(
            Create(function, new McpServerToolCreateOptions
            {
                Name = exportName,
                Description = descriptor.Description,
                UseStructuredContent = true,
                ReadOnly = !_mutation,
                Destructive = _mutation,
                Idempotent = !_mutation,
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
        string? idempotencyKey = null;
        if (_mutation)
        {
            // A retry of the client carries its own operation key; otherwise every call is its own.
            var sent = request.Params?.Meta?[NhAssistantMcpExportCall.IdempotencyKeyMetaKey] is System.Text.Json.Nodes.JsonValue value
                       && value.TryGetValue<string>(out var text)
                ? text
                : null;
            idempotencyKey = NhAssistantMcpExportCall.IsIdempotencyKey(sent) ? sent : Guid.NewGuid().ToString("N");
        }

        using (NhAssistantMcpExportCall.Enter(_actorId, idempotencyKey))
        {
            return await _inner.InvokeAsync(request, cancellationToken);
        }
    }
}
