using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace NewHeap.Platform.AI.Mcp;

public static class NhAiMcpClientExtensions
{
    /// <summary>
    /// Calls a generated NewHeap MCP tool with the default MCP JSON serializer options.
    /// </summary>
    public static ValueTask<TOutput> CallNewHeapToolAsync<TInput, TOutput>(
        this McpClient client,
        string toolName,
        TInput input,
        CancellationToken cancellationToken = default)
    {
        return client.CallNewHeapToolAsync<TInput, TOutput>(
            toolName,
            input,
            McpJsonUtilities.DefaultOptions,
            cancellationToken);
    }

    /// <summary>
    /// Calls a generated NewHeap MCP tool with explicit JSON serializer options.
    /// </summary>
    public static async ValueTask<TOutput> CallNewHeapToolAsync<TInput, TOutput>(
        this McpClient client,
        string toolName,
        TInput input,
        JsonSerializerOptions serializerOptions,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(serializerOptions);

        var result = await client.CallToolAsync(
            toolName,
            new Dictionary<string, object?>
            {
                ["input"] = input
            },
            options: new RequestOptions
            {
                JsonSerializerOptions = serializerOptions
            },
            cancellationToken: cancellationToken);

        if (result.IsError is true)
        {
            throw CreateToolException(toolName, result);
        }

        if (result.StructuredContent is not { ValueKind: JsonValueKind.Object } envelope)
        {
            throw new JsonException(
                $"NewHeap tool '{toolName}' returned no structured TaskResult envelope.");
        }

        if (!envelope.TryGetProperty("success", out var success)
            || success.ValueKind is not JsonValueKind.True)
        {
            throw CreateToolException(toolName, result);
        }

        if (!envelope.TryGetProperty("data", out var data))
        {
            throw new JsonException(
                $"NewHeap tool '{toolName}' returned a successful TaskResult envelope without data.");
        }

        return data.Deserialize<TOutput>(serializerOptions)!;
    }

    private static NhAiMcpToolException CreateToolException(
        string toolName,
        CallToolResult result)
    {
        var message = $"NewHeap tool '{toolName}' failed.";
        if (result.StructuredContent is { ValueKind: JsonValueKind.Object } content
            && content.TryGetProperty("code", out var code)
            && code.ValueKind is JsonValueKind.String
            && !string.IsNullOrWhiteSpace(code.GetString()))
        {
            message = $"NewHeap tool '{toolName}' failed with code '{code.GetString()}'.";
        }

        return new NhAiMcpToolException(toolName, result, message);
    }
}

/// <summary>
/// Represents a failed generated NewHeap MCP tool call while retaining its structured result.
/// </summary>
public sealed class NhAiMcpToolException : McpException
{
    internal NhAiMcpToolException(
        string toolName,
        CallToolResult result,
        string message) : base(message)
    {
        ToolName = toolName;
        Result = result;
    }

    public string ToolName { get; }

    public CallToolResult Result { get; }
}
