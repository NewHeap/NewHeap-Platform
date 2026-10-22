using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.AI.Chat.Runtime;

namespace NewHeap.Platform.AI.Chat.AspNet.Mcp;

/// <summary>
/// The readers an administrator set up on MCP servers (<see cref="NhAssistantMcpReaders"/>), asked by
/// priority, highest first, then by server id and tool name. The runtime, not the model, calls a
/// reader's tool with the file, so the server needs no assignment to the agent. A reader receives a
/// file only when the file's classification is at or below the reader's bound, its server is enabled,
/// the turn's person passes the server's required policy and, in a headless turn, the server allows
/// headless use with the existing consent and initiator rules (Q3, I18, I21). Everything up to the
/// call is checked before the file leaves the platform; a reader that does not pass is passed over
/// with its code. The reading tool must still have the input schema the administrator synced. A
/// personal OAuth server the person of a request turn has not connected asks for the connection as
/// the tool path does (D33).
/// </summary>
internal sealed partial class NhAssistantMcpFileReader(
    NhAssistantAdminStore store,
    NhAssistantMcpToolSource tools,
    NhAssistantMcpConnectionPlanner planner,
    NhAssistantMcpConnectionCache cache,
    IServiceProvider services,
    ILogger<NhAssistantMcpFileReader> logger) : INhAssistantMcpFileReader
{
    /// <summary>
    /// The argument with the file's content, base64-encoded.
    /// </summary>
    public const string ContentArgument = "content";

    /// <summary>
    /// The argument with the file's media type, lowercase and without parameters.
    /// </summary>
    public const string MediaTypeArgument = "mediaType";

    /// <summary>
    /// The argument with the file's name as the application reports it.
    /// </summary>
    public const string NameArgument = "name";

    public async Task<bool> DeclaresAsync(string mediaType, CancellationToken cancellationToken)
    {
        var readers = await store.ListMcpReadersAsync(cancellationToken);
        return readers.Any(item => Reads(item.Reader, mediaType));
    }

    public async Task<NhAssistantFileReading?> ReadAsync(NhAssistantMcpFileReadRequest request, CancellationToken cancellationToken)
    {
        var declared = false;
        NhAssistantFileReading? passedOver = null;
        NhAssistantMcpConnectionNeeded? connectionNeeded = null;
        foreach (var (server, reader) in await store.ListMcpReadersAsync(cancellationToken))
        {
            if (!Reads(reader, request.File.MediaType))
            {
                continue;
            }

            declared = true;
            var reading = await ReadWithAsync(server, reader, request, cancellationToken);
            if (reading.Received || reading.TextCached)
            {
                return reading;
            }

            LogPassedOver(logger, server.Id, reading.Code ?? NhAssistantAdminErrorCodes.McpUnreachable);
            passedOver ??= reading;
            connectionNeeded ??= reading.ConnectionNeeded;
        }

        if (!declared)
        {
            return null;
        }

        // The first reader's code applies; a server the person can connect is still asked for.
        return passedOver! with { ConnectionNeeded = connectionNeeded };
    }

    private async Task<NhAssistantFileReading> ReadWithAsync(
        AssistantMcpServer server,
        AssistantMcpReader reader,
        NhAssistantMcpFileReadRequest request,
        CancellationToken cancellationToken)
    {
        var readerId = NhAssistantMcpReaders.ReaderIdOf(server.Id);
        if (await RefusalAsync(server, reader, request, cancellationToken) is { } refusal)
        {
            return NhAssistantFileReading.PassedOver(readerId, refusal);
        }

        // A server that may receive the file and made a text of the same content earlier in the
        // conversation is not called again; the turn reads its kept text (D32).
        if (request.KeptText is { } keptText && await keptText(readerId, cancellationToken) is { } kept)
        {
            return NhAssistantFileReading.Kept(readerId, kept);
        }

        try
        {
            var plan = await planner.PlanAsync(server, NhAssistantMcpToolSource.SubjectOf(request.Turn), cancellationToken);
            if (!plan.Success)
            {
                return PassedOver(server, readerId, request, NhAssistantResults.CodeOf(plan, NhAssistantAdminErrorCodes.McpUnreachable));
            }

            var lease = await cache.ConnectAsync(server, plan.Data, bypassCache: false, cancellationToken);
            if (!lease.Success)
            {
                return PassedOver(server, readerId, request, NhAssistantResults.CodeOf(lease, NhAssistantAdminErrorCodes.McpUnreachable));
            }

            await using (lease.Data)
            {
                if (await ToolRefusalAsync(server, reader, lease.Data.Tools, cancellationToken) is { } toolRefusal)
                {
                    return NhAssistantFileReading.PassedOver(readerId, toolRefusal);
                }

                return await CallAsync(server, reader, readerId, lease.Data.Client, request, cancellationToken);
            }
        }
        finally
        {
            // A delegated token minted for the connection or the call, or refused, is audited as after
            // the turn's MCP tools.
            await services.GetRequiredService<NhAssistantMcpDelegatedTokenLedger>().FlushAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// A reader passed over before the file left the platform. For a personal OAuth server the person
    /// of a request turn can connect, or connect again, the reading names the server, so the turn shows
    /// the same connection card as for the server's tools.
    /// </summary>
    private static NhAssistantFileReading PassedOver(
        AssistantMcpServer server,
        string readerId,
        NhAssistantMcpFileReadRequest request,
        string code)
    {
        var reading = NhAssistantFileReading.PassedOver(readerId, code);
        if (NhAssistantMcpToolSource.PersonalConnectionReason(server, request.Turn, code) is not { } reason)
        {
            return reading;
        }

        return reading with { ConnectionNeeded = new NhAssistantMcpConnectionNeeded(server.Id, server.DisplayName, reason) };
    }

    /// <summary>
    /// Why the reader may not receive the file in this turn, or <see langword="null"/> when it may:
    /// a classification above its bound, a headless turn its server does not serve (headless use,
    /// consent, initiator) or a person who does not pass the server's required policy.
    /// </summary>
    private async Task<string?> RefusalAsync(
        AssistantMcpServer server,
        AssistantMcpReader reader,
        NhAssistantMcpFileReadRequest request,
        CancellationToken cancellationToken)
    {
        if (!NhAssistantDataClassificationNames.TryParse(reader.MaxClassification, out var bound)
            || request.File.Classification > bound)
        {
            return NhAssistantErrorCodes.ClassificationNotPermitted;
        }
        if (request.Turn.IsHeadless && await tools.HeadlessRefusalAsync(server, request.Turn, cancellationToken) is { } headless)
        {
            return headless;
        }

        return await tools.CanUseAsync(server, request.Turn, cancellationToken)
            ? null
            : NhAssistantAdminErrorCodes.Forbidden;
    }

    /// <summary>
    /// Why the reader's tool may not be called: the server no longer offers it, or its input schema
    /// differs from the one the administrator synced.
    /// </summary>
    private async Task<string?> ToolRefusalAsync(
        AssistantMcpServer server,
        AssistantMcpReader reader,
        IList<McpClientTool> remoteTools,
        CancellationToken cancellationToken)
    {
        var remote = remoteTools.FirstOrDefault(tool => string.Equals(tool.Name, reader.ToolName, StringComparison.Ordinal));
        if (remote is null)
        {
            return NhAssistantAdminErrorCodes.McpToolNotFound;
        }

        var reviewed = (await store.ListMcpToolsAsync(server.Id, cancellationToken))
            .FirstOrDefault(tool => string.Equals(tool.RemoteName, reader.ToolName, StringComparison.Ordinal));
        return reviewed is not null && string.Equals(reviewed.InputSchemaHash, NhAssistantMcpToolSource.SchemaHash(remote), StringComparison.Ordinal)
            ? null
            : NhAssistantAdminErrorCodes.McpSchemaChanged;
    }

    /// <summary>
    /// Sends the file to the reader's tool and returns the text of its result. A failure after the
    /// request may have reached the server is final: the file is not sent to another reader.
    /// </summary>
    private async Task<NhAssistantFileReading> CallAsync(
        AssistantMcpServer server,
        AssistantMcpReader reader,
        string readerId,
        McpClient client,
        NhAssistantMcpFileReadRequest request,
        CancellationToken cancellationToken)
    {
        var owner = tools.OwnerOf(server, request.Turn);
        var file = request.File;
        var arguments = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [NameArgument] = file.Reference.Name,
            [MediaTypeArgument] = file.MediaType,
            [ContentArgument] = Convert.ToBase64String(file.Content.Span)
        };

        CallToolResult result;
        try
        {
            result = await client.CallToolAsync(reader.ToolName, arguments, cancellationToken: cancellationToken);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException
            && !(exception is OperationCanceledException && cancellationToken.IsCancellationRequested))
        {
            var code = NhAssistantMcpFailures.Classify(exception);
            if (NhAssistantMcpFailures.ToolNotRun(exception))
            {
                // The request never reached the tool, so the file did not leave the platform.
                return PassedOver(server, readerId, request, code);
            }

            LogReaderFailed(logger, server.Id, code);
            return NhAssistantFileReading.Failed(readerId, owner);
        }

        var texts = result.Content.OfType<TextContentBlock>().Select(block => block.Text).ToArray();
        if (result.IsError is true || texts.Length == 0)
        {
            // The remote text of a failure may echo the file: it is never logged or shown.
            LogReaderFailed(logger, server.Id, result.IsError is true ? "tool-error" : "no-text");
            return NhAssistantFileReading.Failed(readerId, owner);
        }

        return NhAssistantFileReading.Read(readerId, string.Join("\n", texts), owner);
    }

    private static bool Reads(AssistantMcpReader reader, string mediaType)
    {
        return NhAssistantMcpReaders.Split(reader.MediaTypes).Contains(mediaType, StringComparer.Ordinal);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "MCP server {ServerId} was passed over as reader for a file ({Code}).")]
    private static partial void LogPassedOver(ILogger logger, string serverId, string code);

    [LoggerMessage(Level = LogLevel.Warning, Message = "MCP server {ServerId} failed to read a file ({Code}).")]
    private static partial void LogReaderFailed(ILogger logger, string serverId, string code);
}
