using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Extensions.Tasks;
using ModelContextProtocol.Protocol;
using NewHeap.Platform.AI.Mcp;

namespace NewHeap.Platform.AI.Chat.AspNet.Mcp;

/// <summary>
/// Calls a tool of an imported MCP server as an MCP task (SEP-2663) when the server offers tasks on a
/// connection of a protocol revision with tasks: the server answers with a task for a long tool, and the
/// assistant polls it within the same cancellation as any call, which carries the tool's timeout and the
/// turn's budget. A server without tasks, or a tool it answers at once, is called as before. The server's
/// poll interval is bounded, a task that asks for input is cancelled, because the assistant has nobody to
/// answer it, and a call that ends early cancels its task on the server. What the server returns stays
/// untrusted data, as every result of an imported tool.
/// </summary>
internal static class NhAssistantMcpRemoteTasks
{
    /// <summary>The code of a remote task that failed, was cancelled or asked for input.</summary>
    public const string TaskEndedCode = "ai-mcp-remote-task-ended";

    internal static readonly TimeSpan MinPollInterval = TimeSpan.FromMilliseconds(250);
    internal static readonly TimeSpan MaxPollInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan CancelTimeout = TimeSpan.FromSeconds(5);

    public static NhAiMcpRemoteCall For(McpClient client, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        var log = logger ?? NullLogger.Instance;
        return (remote, arguments, options, cancellationToken) => CallAsync(client, remote, arguments, options, log, cancellationToken);
    }

    /// <summary>
    /// Whether the connection negotiated a revision with tasks and the server offers the extension.
    /// </summary>
    public static bool OffersTasks(McpClient client)
    {
        return client.NegotiatedProtocolVersion is { Length: 10 } version
            && string.CompareOrdinal(version, "2026-07-28") >= 0
            && client.ServerCapabilities?.Extensions?.ContainsKey(TasksProtocol.ExtensionId) is true;
    }

    private static async ValueTask<CallToolResult> CallAsync(
        McpClient client,
        McpClientTool remote,
        IReadOnlyDictionary<string, object?> arguments,
        RequestOptions? options,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        if (!OffersTasks(client))
        {
            return await remote.CallAsync(arguments, progress: null, options: options, cancellationToken: cancellationToken);
        }

        var started = await client.CallToolAsTaskAsync(
            new CallToolRequestParams
            {
                Name = remote.ProtocolTool.Name,
                Arguments = arguments.ToDictionary(
                    argument => argument.Key,
                    argument => JsonSerializer.SerializeToElement(argument.Value, remote.JsonSerializerOptions),
                    StringComparer.Ordinal),
                Meta = options?.Meta
            },
            cancellationToken);
        if (!started.IsTask)
        {
            return started.Result!;
        }

        var task = started.TaskCreated!;
        var finished = false;
        try
        {
            var result = await PollAsync(client, task.TaskId, task.PollIntervalMs, logger, cancellationToken);
            finished = true;
            return result;
        }
        finally
        {
            if (!finished)
            {
                await CancelQuietlyAsync(client, task.TaskId, logger);
            }
        }
    }

    private static async Task<CallToolResult> PollAsync(
        McpClient client,
        string taskId,
        long? pollIntervalMs,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var interval = Bounded(pollIntervalMs);
        while (true)
        {
            await Task.Delay(interval, cancellationToken);
            var state = await client.GetTaskAsync(taskId, cancellationToken);
            interval = Bounded(state.PollIntervalMs ?? (long)interval.TotalMilliseconds);
            switch (state)
            {
                case WorkingTaskResult:
                    continue;
                case CompletedTaskResult completed:
                    return completed.Result.Deserialize<CallToolResult>(McpJsonUtilities.DefaultOptions)
                        ?? throw new JsonException("The remote MCP task completed without a tool result.");
                case InputRequiredTaskResult:
                    // Nobody answers a remote server's question in a turn; the call ends and the task is cancelled.
                    logger.LogInformation("The remote MCP task {TaskId} asked for input and is cancelled.", taskId);
                    await CancelQuietlyAsync(client, taskId, logger);
                    return Ended("The remote MCP tool asked for input, which the assistant cannot give.");
                case FailedTaskResult:
                    return Ended("The remote MCP tool's task failed.");
                case CancelledTaskResult:
                    return Ended("The remote MCP tool's task was cancelled.");
                default:
                    return Ended("The remote MCP tool's task ended without a result.");
            }
        }
    }

    private static TimeSpan Bounded(long? pollIntervalMs)
    {
        var requested = TimeSpan.FromMilliseconds(Math.Max(0, pollIntervalMs ?? (long)MinPollInterval.TotalMilliseconds));
        if (requested < MinPollInterval)
        {
            return MinPollInterval;
        }

        return requested > MaxPollInterval ? MaxPollInterval : requested;
    }

    private static CallToolResult Ended(string message)
    {
        return new CallToolResult
        {
            IsError = true,
            Content = [new TextContentBlock { Text = $"{TaskEndedCode}: {message}" }]
        };
    }

    private static async Task CancelQuietlyAsync(McpClient client, string taskId, ILogger logger)
    {
        using var timeout = new CancellationTokenSource(CancelTimeout);
        try
        {
            await client.CancelTaskAsync(taskId, timeout.Token);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Cancellation is cooperative; the server's own time to live ends the task anyway.
            logger.LogInformation("Cancelling the remote MCP task {TaskId} failed with {ExceptionType}.", taskId, exception.GetType().Name);
        }
    }
}
