using System.Buffers;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.AI;
using NewHeap.Platform.Common.Models;

namespace NewHeap.Platform.AI;

/// <summary>
/// The result every governed tool returns to a model or protocol caller. A success is
/// <c>{"success":true,"data":...}</c>; a failure is
/// <c>{"success":false,"code":"...","message":"..."}</c>, with <c>data</c> only when the failed
/// <see cref="TaskResult{T}"/> carries a typed payload. The code is the stable code of the outcome,
/// so a model can tell one expected failure from another and explain or recover from it.
/// </summary>
/// <remarks>
/// The envelope property names are fixed; only <c>data</c> follows the tool's serializer options.
/// The shape is not part of a tool's contract hash: it is the same for every governed tool. A
/// success may name files next to its data (<c>"files":[{"id":...,"name":...,"mediaType":...,"size":...,"hash":...}]</c>);
/// the assistant opens them through the application's file source and gives them to the model.
/// </remarks>
public static class NhAiToolResult
{
    public const string SuccessPropertyName = "success";
    public const string CodePropertyName = "code";
    public const string MessagePropertyName = "message";
    public const string DataPropertyName = "data";

    /// <summary>
    /// The envelope property that names the files of a successful result.
    /// </summary>
    public const string FilesPropertyName = "files";

    /// <summary>
    /// The most files one result names.
    /// </summary>
    public const int MaxFiles = 16;

    private const string FileIdPropertyName = "id";
    private const string FileNamePropertyName = "name";
    private const string FileMediaTypePropertyName = "mediaType";
    private const string FileSizePropertyName = "size";
    private const string FileHashPropertyName = "hash";

    private const int DefaultMaxDepth = 64;

    /// <summary>
    /// Writes a tool outcome in the shared shape. The code is the first result item named with a
    /// lowercase dash-case code, otherwise <see cref="NhAiToolFailureCodes.Failed"/>; the message is
    /// that item's messages, or every message with its item name when no item carries a code. A
    /// success whose data implements <see cref="INhAiToolResultFiles"/> names its files.
    /// </summary>
    public static JsonElement Create<T>(TaskResult<T> result, JsonSerializerOptions serializerOptions)
    {
        ArgumentNullException.ThrowIfNull(result);
        var files = result.Success && result.Data is INhAiToolResultFiles named
            ? named.Files
            : [];
        return Create(result, serializerOptions, files);
    }

    /// <summary>
    /// Writes a successful tool outcome in the shared shape with the files it names next to its
    /// data; a failure names no files. Every reference must be valid
    /// (<see cref="NhAiFileReference.IsValid"/>) and a result names at most <see cref="MaxFiles"/>.
    /// </summary>
    public static JsonElement Create<T>(
        TaskResult<T> result,
        JsonSerializerOptions serializerOptions,
        IReadOnlyList<NhAiFileReference> files)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(serializerOptions);
        ArgumentNullException.ThrowIfNull(files);
        RequireTypeInfoResolver(serializerOptions, nameof(serializerOptions));
        if (files.Count > MaxFiles || files.Any(file => !NhAiFileReference.IsValid(file)))
        {
            throw new ArgumentException(
                $"A tool result names at most {MaxFiles} files, each with a bounded id, name and hash, a valid media type and a size of zero or more.",
                nameof(files));
        }

        var dataTypeInfo = (JsonTypeInfo<T>)serializerOptions.GetTypeInfo(typeof(T));
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Encoder = serializerOptions.Encoder }))
        {
            writer.WriteStartObject();
            writer.WriteBoolean(SuccessPropertyName, result.Success);
            if (!result.Success)
            {
                WriteFailure(writer, GetCode(result), GetMessage(result));
            }

            // A success always carries data; a failure only when it has a typed payload.
            if (result.Success || result.Data is not null)
            {
                writer.WritePropertyName(DataPropertyName);
                JsonSerializer.Serialize(writer, result.Data!, dataTypeInfo);
            }
            if (result.Success && files.Count > 0)
            {
                WriteFiles(writer, files);
            }
            writer.WriteEndObject();
        }

        return Parse(buffer, serializerOptions.MaxDepth);
    }

    /// <summary>
    /// Reads the files a successful result names. Returns <see langword="true"/> with an empty list
    /// for a failure or a result without files, and <see langword="false"/> when <c>files</c> is not
    /// a list of at most <see cref="MaxFiles"/> valid references.
    /// </summary>
    public static bool TryGetFiles(JsonElement result, out IReadOnlyList<NhAiFileReference> files)
    {
        files = [];
        if (result.ValueKind != JsonValueKind.Object
            || !result.TryGetProperty(SuccessPropertyName, out var success)
            || success.ValueKind != JsonValueKind.True
            || !result.TryGetProperty(FilesPropertyName, out var named))
        {
            return true;
        }
        if (named.ValueKind != JsonValueKind.Array || named.GetArrayLength() > MaxFiles)
        {
            return false;
        }

        var references = new List<NhAiFileReference>(named.GetArrayLength());
        foreach (var item in named.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object
                || Text(item, FileIdPropertyName) is not { } id
                || Text(item, FileNamePropertyName) is not { } name
                || Text(item, FileMediaTypePropertyName) is not { } mediaType
                || !item.TryGetProperty(FileSizePropertyName, out var sizeValue)
                || sizeValue.ValueKind != JsonValueKind.Number
                || !sizeValue.TryGetInt64(out var size))
            {
                return false;
            }

            string? hash = null;
            if (item.TryGetProperty(FileHashPropertyName, out var hashValue) && hashValue.ValueKind != JsonValueKind.Null)
            {
                if (hashValue.ValueKind != JsonValueKind.String)
                {
                    return false;
                }
                hash = hashValue.GetString();
            }

            var reference = new NhAiFileReference(id, name, mediaType, size, hash);
            if (!NhAiFileReference.IsValid(reference))
            {
                return false;
            }
            references.Add(reference);
        }

        files = references;
        return true;
    }

    /// <summary>
    /// Writes a failure that did not come from a <see cref="TaskResult"/>, such as a call refused
    /// before the tool ran.
    /// </summary>
    public static JsonElement Failed(string code, string? message)
    {
        NhAiNames.ValidateSegment(code, nameof(code));

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteBoolean(SuccessPropertyName, false);
            WriteFailure(writer, code, string.IsNullOrWhiteSpace(message) ? null : message);
            writer.WriteEndObject();
        }

        return Parse(buffer, 0);
    }

    /// <summary>
    /// Creates the <see cref="AIFunctionFactoryOptions.MarshalResult"/> delegate of a function whose
    /// handler returns <see cref="TaskResult{T}"/>: the function then returns the shared shape, with
    /// <c>data</c> written through <paramref name="serializerOptions"/>. Combine it with
    /// <see cref="AIFunctionFactoryOptions.ExcludeResultSchema"/>; the governed function publishes
    /// <see cref="CreateJsonSchema"/> as its return schema.
    /// </summary>
    /// <param name="serializerOptions">
    /// The options that serialize <typeparamref name="T"/>, normally the function's own
    /// <see cref="AIFunctionFactoryOptions.SerializerOptions"/>; <see cref="AIJsonUtilities.DefaultOptions"/>
    /// when <see langword="null"/>. They need a <see cref="JsonSerializerOptions.TypeInfoResolver"/>;
    /// only <typeparamref name="T"/> needs metadata in a source-generated context.
    /// </param>
    public static Func<object?, Type?, CancellationToken, ValueTask<object?>> CreateMarshaller<T>(
        JsonSerializerOptions? serializerOptions = null)
    {
        var options = serializerOptions ?? AIJsonUtilities.DefaultOptions;
        RequireTypeInfoResolver(options, nameof(serializerOptions));

        return (value, _, _) =>
        {
            if (value is not TaskResult<T> result)
            {
                throw new InvalidOperationException(
                    $"An AI tool handler returned no TaskResult<{typeof(T).Name}>.");
            }

            return new ValueTask<object?>(Create(result, options));
        };
    }

    /// <summary>
    /// The JSON schema of the shared shape around a tool's output schema, as published for the
    /// return value of a governed function.
    /// </summary>
    public static JsonElement CreateJsonSchema(string? dataSchemaJson)
    {
        JsonNode data;
        try
        {
            data = string.IsNullOrWhiteSpace(dataSchemaJson)
                ? new JsonObject()
                : JsonNode.Parse(dataSchemaJson) ?? new JsonObject();
        }
        catch (JsonException exception)
        {
            throw new ArgumentException("The tool output schema is not valid JSON.", nameof(dataSchemaJson), exception);
        }

        var schema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                [SuccessPropertyName] = new JsonObject
                {
                    ["type"] = "boolean",
                    ["description"] = "Whether the tool completed. When false, nothing in data describes a completed action."
                },
                [CodePropertyName] = new JsonObject
                {
                    ["type"] = "string",
                    ["description"] = "The stable failure code; present only when success is false."
                },
                [MessagePropertyName] = new JsonObject
                {
                    ["type"] = "string",
                    ["description"] = "A safe explanation of the failure; present only when success is false."
                },
                [DataPropertyName] = data,
                [FilesPropertyName] = new JsonObject
                {
                    ["type"] = "array",
                    ["description"] = "Files a successful result names; present only when it names files.",
                    ["items"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            [FileIdPropertyName] = new JsonObject { ["type"] = "string" },
                            [FileNamePropertyName] = new JsonObject { ["type"] = "string" },
                            [FileMediaTypePropertyName] = new JsonObject { ["type"] = "string" },
                            [FileSizePropertyName] = new JsonObject { ["type"] = "integer" },
                            [FileHashPropertyName] = new JsonObject { ["type"] = "string" }
                        },
                        ["required"] = new JsonArray(FileIdPropertyName, FileNamePropertyName, FileMediaTypePropertyName, FileSizePropertyName)
                    }
                }
            },
            ["required"] = new JsonArray(SuccessPropertyName)
        };
        using var document = JsonDocument.Parse(schema.ToJsonString());
        return document.RootElement.Clone();
    }

    internal static string GetCode(TaskResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return FindCodedItem(result)?.Name ?? NhAiToolFailureCodes.Failed;
    }

    internal static string? GetMessage(TaskResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var coded = FindCodedItem(result);
        IEnumerable<string> messages;
        if (coded is not null)
        {
            messages = coded.ErrorMessages.Select(Format);
        }
        else
        {
            // Without a code, keep every message and the field it belongs to.
            messages = result.GetResultItems().SelectMany(item => item.ErrorMessages.Select(message =>
                string.IsNullOrWhiteSpace(item.Name) ? Format(message) : item.Name + ": " + Format(message)));
        }

        var text = string.Join(" ", messages.Where(message => !string.IsNullOrWhiteSpace(message)));
        return text.Length == 0 ? null : text;
    }

    private static TaskResult.ResultItem? FindCodedItem(TaskResult result)
    {
        return result.GetResultItems().FirstOrDefault(item => NhAiNames.IsSegment(item.Name));
    }

    private static string Format(FormattableString message)
    {
        return message.ToString(CultureInfo.InvariantCulture);
    }

    private static void RequireTypeInfoResolver(JsonSerializerOptions options, string parameterName)
    {
        // Resolving metadata without a resolver would need reflection the caller did not choose.
        if (options.TypeInfoResolver is null)
        {
            throw new ArgumentException(
                "The serializer options need a TypeInfoResolver, such as a JsonSerializerContext or AIJsonUtilities.DefaultOptions.TypeInfoResolver.",
                parameterName);
        }
    }

    private static void WriteFiles(Utf8JsonWriter writer, IReadOnlyList<NhAiFileReference> files)
    {
        writer.WriteStartArray(FilesPropertyName);
        foreach (var file in files)
        {
            writer.WriteStartObject();
            writer.WriteString(FileIdPropertyName, file.Id);
            writer.WriteString(FileNamePropertyName, file.Name);
            writer.WriteString(FileMediaTypePropertyName, file.MediaType);
            writer.WriteNumber(FileSizePropertyName, file.Size);
            if (file.Hash is not null)
            {
                writer.WriteString(FileHashPropertyName, file.Hash);
            }
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }

    private static string? Text(JsonElement item, string propertyName)
    {
        return item.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static void WriteFailure(Utf8JsonWriter writer, string code, string? message)
    {
        writer.WriteString(CodePropertyName, code);
        if (message is not null)
        {
            writer.WriteString(MessagePropertyName, message);
        }
    }

    private static JsonElement Parse(ArrayBufferWriter<byte> buffer, int dataMaxDepth)
    {
        // The envelope adds one level to the deepest payload the serializer accepted.
        var maxDepth = (dataMaxDepth > 0 ? dataMaxDepth : DefaultMaxDepth) + 1;
        using var document = JsonDocument.Parse(buffer.WrittenMemory, new JsonDocumentOptions { MaxDepth = maxDepth });
        return document.RootElement.Clone();
    }
}
