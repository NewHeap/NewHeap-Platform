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
    /// A failed result becomes <see cref="NhAiMcpToolException{TPayload}"/> when the failed
    /// <c>TaskResult</c> carried data, otherwise <see cref="NhAiMcpToolException"/>.
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

        JsonElement? envelope = result.StructuredContent is { ValueKind: JsonValueKind.Object } content
            ? content
            : null;
        JsonElement? failedData = null;
        if (envelope is { } failedEnvelope
            && NhAiMcpResultMetadata.TryGetEnvelopeProperty(failedEnvelope, "data", out var envelopeData)
            && envelopeData.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
        {
            failedData = envelopeData;
        }

        if (result.IsError is true)
        {
            throw CreateToolException<TOutput>(toolName, result, failedData, serializerOptions);
        }

        if (envelope is not { } successEnvelope)
        {
            throw new JsonException(
                $"NewHeap tool '{toolName}' returned no structured TaskResult envelope.");
        }

        if (!NhAiMcpResultMetadata.TryGetEnvelopeProperty(successEnvelope, "success", out var success)
            || success.ValueKind is not JsonValueKind.True)
        {
            throw CreateToolException<TOutput>(toolName, result, failedData, serializerOptions);
        }

        if (!NhAiMcpResultMetadata.TryGetEnvelopeProperty(successEnvelope, "data", out var data))
        {
            throw new JsonException(
                $"NewHeap tool '{toolName}' returned a successful TaskResult envelope without data.");
        }

        return data.Deserialize<TOutput>(serializerOptions)!;
    }

    /// <summary>
    /// Calls a generated NewHeap MCP tool exported with <see cref="NhAiToolExportSchema.Flat"/>
    /// using the default MCP JSON serializer options.
    /// </summary>
    public static ValueTask<TOutput> CallNewHeapFlatToolAsync<TInput, TOutput>(
        this McpClient client,
        string toolName,
        TInput input,
        CancellationToken cancellationToken = default)
    {
        return client.CallNewHeapFlatToolAsync<TInput, TOutput>(
            toolName,
            input,
            McpJsonUtilities.DefaultOptions,
            cancellationToken);
    }

    /// <summary>
    /// Calls a generated NewHeap MCP tool exported with <see cref="NhAiToolExportSchema.Flat"/>.
    /// The input properties become the top-level arguments and the structured content is the
    /// typed output. Use the tool set's serializer options so argument and result names match
    /// its wire contract. A failed tool result becomes
    /// <see cref="NhAiMcpToolException{TPayload}"/> when it carries a typed payload, otherwise
    /// <see cref="NhAiMcpToolException"/>; both expose the failure code, message and evidence
    /// reference.
    /// </summary>
    public static async ValueTask<TOutput> CallNewHeapFlatToolAsync<TInput, TOutput>(
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

        var inputElement = JsonSerializer.SerializeToElement(input, serializerOptions);
        if (inputElement.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException(
                "Flat NewHeap tool input must serialize to a JSON object.",
                nameof(input));
        }
        var arguments = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var property in inputElement.EnumerateObject())
        {
            arguments[property.Name] = property.Value;
        }

        // The arguments are already JSON; the protocol request uses the MCP defaults so a
        // source-generated tool context does not need metadata for the request envelope.
        var result = await client.CallToolAsync(
            toolName,
            arguments,
            cancellationToken: cancellationToken);

        JsonElement? content = result.StructuredContent is { } structured
            && structured.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null)
                ? structured
                : null;
        if (result.IsError is true)
        {
            var payload = NhAiMcpToolFailureDetail.CarriesTypedPayload(result) ? content : null;
            throw CreateToolException<TOutput>(toolName, result, payload, serializerOptions);
        }

        if (content is not { } output)
        {
            throw new JsonException(
                $"NewHeap tool '{toolName}' returned no structured flat result.");
        }

        return output.Deserialize<TOutput>(serializerOptions)!;
    }

    private static NhAiMcpToolException CreateToolException<TPayload>(
        string toolName,
        CallToolResult result,
        JsonElement? payloadJson,
        JsonSerializerOptions serializerOptions)
    {
        var detail = NhAiMcpToolFailureDetail.From(result);
        if (payloadJson is { } json)
        {
            TPayload? payload;
            try
            {
                payload = json.Deserialize<TPayload>(serializerOptions);
            }
            catch (JsonException)
            {
                // A payload that does not match the output contract stays available as JSON.
                return new NhAiMcpToolException(toolName, result, detail, json);
            }
            return new NhAiMcpToolException<TPayload>(toolName, result, detail, json, payload);
        }

        return new NhAiMcpToolException(toolName, result, detail, null);
    }
}

internal sealed record NhAiMcpToolFailureDetail(
    string? Code,
    string? Message,
    string? EvidenceReference)
{
    public static NhAiMcpToolFailureDetail From(CallToolResult result)
    {
        var code = ReadMeta(result, NhAiMcpResultMetadata.CodeKey);
        var message = ReadMeta(result, NhAiMcpResultMetadata.MessageKey);
        var evidenceReference = ReadMeta(result, NhAiMcpResultMetadata.EvidenceReferenceKey);

        // Failures without a typed payload publish their detail as top-level structured content.
        if (result.StructuredContent is { ValueKind: JsonValueKind.Object } content)
        {
            code ??= ReadString(content, "code");
            message ??= ReadString(content, "message");
            evidenceReference ??= ReadString(content, "evidenceReference");
        }

        return new NhAiMcpToolFailureDetail(code, message, evidenceReference);
    }

    /// <summary>
    /// Whether the structured content of a failed flat result is the tool's typed payload rather
    /// than the NewHeap error payload. Servers that publish no NewHeap failure metadata only ever
    /// placed a typed payload in structured content.
    /// </summary>
    public static bool CarriesTypedPayload(CallToolResult result)
    {
        if (result.Meta is null)
        {
            return true;
        }
        if (result.Meta.TryGetPropertyValue(NhAiMcpResultMetadata.TypedPayloadKey, out var marker)
            && marker is not null)
        {
            try
            {
                return marker.GetValue<bool>();
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }
        return !result.Meta.ContainsKey(NhAiMcpResultMetadata.CodeKey);
    }

    private static string? ReadMeta(CallToolResult result, string key)
    {
        if (result.Meta is null
            || !result.Meta.TryGetPropertyValue(key, out var node)
            || node is null)
        {
            return null;
        }
        try
        {
            var value = node.GetValue<string>();
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static string? ReadString(JsonElement content, string name)
    {
        return content.TryGetProperty(name, out var value)
            && value.ValueKind is JsonValueKind.String
            && !string.IsNullOrWhiteSpace(value.GetString())
                ? value.GetString()
                : null;
    }
}

/// <summary>
/// Represents a failed generated NewHeap MCP tool call while retaining its structured result
/// and the stable failure detail the server published.
/// </summary>
public class NhAiMcpToolException : McpException
{
    internal NhAiMcpToolException(
        string toolName,
        CallToolResult result,
        NhAiMcpToolFailureDetail detail,
        JsonElement? payloadJson)
        : base(FormatMessage(toolName, detail))
    {
        ToolName = toolName;
        Result = result;
        Code = detail.Code;
        FailureMessage = detail.Message;
        EvidenceReference = detail.EvidenceReference;
        PayloadJson = payloadJson;
    }

    public string ToolName { get; }

    public CallToolResult Result { get; }

    /// <summary>The stable failure code, such as a consumer denial code or an <c>ai-tool-*</c> pipeline code.</summary>
    public string? Code { get; }

    /// <summary>The safe failure message published by the tool, without the tool name or code.</summary>
    public string? FailureMessage { get; }

    /// <summary>The bounded evidence reference attached to the failure, when the server published one.</summary>
    public string? EvidenceReference { get; }

    /// <summary>The typed failure payload as JSON, when the failed result carried data.</summary>
    public JsonElement? PayloadJson { get; }

    private static string FormatMessage(string toolName, NhAiMcpToolFailureDetail detail)
    {
        var message = $"NewHeap tool '{toolName}' failed";
        if (!string.IsNullOrWhiteSpace(detail.Code))
        {
            message += $" with code '{detail.Code}'";
        }
        if (!string.IsNullOrWhiteSpace(detail.Message))
        {
            message += $": {detail.Message}";
        }
        else
        {
            message += ".";
        }
        return message;
    }
}

/// <summary>
/// A failed generated NewHeap MCP tool call whose result carried a typed failure payload,
/// such as a consumer receipt for a denied approval.
/// </summary>
public sealed class NhAiMcpToolException<TPayload> : NhAiMcpToolException
{
    internal NhAiMcpToolException(
        string toolName,
        CallToolResult result,
        NhAiMcpToolFailureDetail detail,
        JsonElement payloadJson,
        TPayload? payload)
        : base(toolName, result, detail, payloadJson)
    {
        Payload = payload;
    }

    /// <summary>The typed failure payload deserialized with the caller's serializer options.</summary>
    public TPayload? Payload { get; }
}
