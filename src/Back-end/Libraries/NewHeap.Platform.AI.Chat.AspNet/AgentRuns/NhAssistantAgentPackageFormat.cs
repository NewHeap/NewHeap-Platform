using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using NewHeap.Platform.Common.Models;

namespace NewHeap.Platform.AI.Chat.AspNet.AgentRuns;

/// <summary>
/// The agent package format (PK3): its name and version, its limits, strict reading, writing and the
/// SHA-256 of a part's canonical JSON. An application or a build tool may write the same JSON itself,
/// for example to ship packages with a deploy; the consumer guide publishes its JSON schema.
/// </summary>
public static class NhAssistantAgentPackageFormat
{
    /// <summary>The value of <see cref="NhAssistantAgentPackage.Format"/>.</summary>
    public const string Name = "newheap.assistant.agent-package";

    /// <summary>The format version this host reads and writes.</summary>
    public const int Version = 1;

    /// <summary>The largest package in bytes of its UTF-8 JSON: 1 MB.</summary>
    public const int MaxBytes = 1_048_576;

    /// <summary>The most agents of one package.</summary>
    public const int MaxAgents = 20;

    /// <summary>The longest package id.</summary>
    public const int MaxIdLength = 128;

    /// <summary>The longest package description.</summary>
    public const int MaxDescriptionLength = 500;

    private static readonly JavaScriptEncoder CanonicalEncoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping;

    /// <summary>
    /// Reads a package from its UTF-8 JSON, strictly: a package larger than <see cref="MaxBytes"/> or
    /// with more than <see cref="MaxAgents"/> agents fails with <c>assistant-package-too-large</c>; an
    /// unknown property, a missing required one, another <c>format</c> or a newer
    /// <c>formatVersion</c> with <c>assistant-package-format-unsupported</c>.
    /// </summary>
    public static TaskResult<NhAssistantAgentPackage> Read(ReadOnlySpan<byte> utf8Json)
    {
        if (utf8Json.Length > MaxBytes)
        {
            return TooLarge();
        }

        try
        {
            using var document = JsonDocument.Parse(utf8Json.ToArray());
            return Read(document.RootElement);
        }
        catch (JsonException)
        {
            return Unsupported();
        }
    }

    /// <summary>
    /// Reads a package from its JSON text, as <see cref="Read(ReadOnlySpan{byte})"/> does.
    /// </summary>
    public static TaskResult<NhAssistantAgentPackage> Read(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        return Read(Encoding.UTF8.GetBytes(json));
    }

    /// <summary>
    /// Reads a package from a JSON element, as <see cref="Read(ReadOnlySpan{byte})"/> does.
    /// </summary>
    public static TaskResult<NhAssistantAgentPackage> Read(JsonElement json)
    {
        if (json.ValueKind != JsonValueKind.Object)
        {
            return Unsupported();
        }
        if (Encoding.UTF8.GetByteCount(json.GetRawText()) > MaxBytes)
        {
            return TooLarge();
        }

        // The format and its version first, so a newer package says so instead of failing on a
        // property this host does not know.
        if (!json.TryGetProperty("format", out var format)
            || format.ValueKind != JsonValueKind.String
            || !string.Equals(format.GetString(), Name, StringComparison.Ordinal)
            || !json.TryGetProperty("formatVersion", out var formatVersion)
            || formatVersion.ValueKind != JsonValueKind.Number
            || !formatVersion.TryGetInt32(out var version)
            || version is < 1 or > Version)
        {
            return Unsupported();
        }

        NhAssistantAgentPackage? package;
        try
        {
            package = json.Deserialize(NhAssistantAgentPackageFormatJsonSerializerContext.Default.NhAssistantAgentPackage);
        }
        catch (JsonException)
        {
            return Unsupported();
        }

        return Check(package);
    }

    /// <summary>
    /// Checks a package an application built in code as reading checks one: the format and its
    /// version, and the number of agents.
    /// </summary>
    public static TaskResult<NhAssistantAgentPackage> Check(NhAssistantAgentPackage? package)
    {
        if (package is null
            || !string.Equals(package.Format, Name, StringComparison.Ordinal)
            || package.FormatVersion is < 1 or > Version
            || package.Package is null
            || package.Agents is null
            || package.Agents.Any(agent => agent is null))
        {
            return Unsupported();
        }
        if (package.Agents.Count > MaxAgents)
        {
            return TooLarge();
        }

        return TaskResult<NhAssistantAgentPackage>.Succeeded(package);
    }

    /// <summary>
    /// The package's JSON as the export downloads it: indented, without <see langword="null"/> values.
    /// </summary>
    public static string Write(NhAssistantAgentPackage package)
    {
        ArgumentNullException.ThrowIfNull(package);
        return JsonSerializer.Serialize(package, NhAssistantAgentPackageFormatJsonSerializerContext.Default.NhAssistantAgentPackage);
    }

    /// <summary>
    /// The SHA-256 of the package's canonical JSON, lowercase hex.
    /// </summary>
    public static string HashOf(NhAssistantAgentPackage package)
    {
        ArgumentNullException.ThrowIfNull(package);
        return HashOf(JsonSerializer.SerializeToElement(package, NhAssistantAgentPackageFormatJsonSerializerContext.Default.NhAssistantAgentPackage));
    }

    /// <summary>
    /// The SHA-256 of a part's canonical JSON, lowercase hex: the part as the format writes it, without
    /// <see langword="null"/> values, with object properties ordered by name (ordinal), without
    /// white space between tokens, and strings escaped only where JSON requires it.
    /// </summary>
    public static string HashOf<T>(T part)
    {
        ArgumentNullException.ThrowIfNull(part);
        var typeInfo = (JsonTypeInfo<T>)NhAssistantAgentPackageFormatJsonSerializerContext.Default.GetTypeInfo(typeof(T))!;
        return HashOf(JsonSerializer.SerializeToElement(part, typeInfo));
    }

    /// <summary>
    /// The SHA-256 of the canonical form of a JSON value, lowercase hex.
    /// </summary>
    public static string HashOf(JsonElement json)
    {
        return Convert.ToHexStringLower(SHA256.HashData(Canonical(json)));
    }

    /// <summary>
    /// The canonical UTF-8 JSON of a value: object properties ordered by name (ordinal), properties
    /// with a <see langword="null"/> value left out, no white space.
    /// </summary>
    internal static byte[] Canonical(JsonElement json)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Encoder = CanonicalEncoder, Indented = false }))
        {
            WriteCanonical(writer, json);
        }

        return buffer.WrittenSpan.ToArray();
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement json)
    {
        switch (json.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in json.EnumerateObject()
                             .Where(property => property.Value.ValueKind != JsonValueKind.Null)
                             .OrderBy(property => property.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(writer, property.Value);
                }

                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in json.EnumerateArray())
                {
                    WriteCanonical(writer, item);
                }

                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(json.GetString());
                break;
            case JsonValueKind.Number:
                writer.WriteRawValue(json.GetRawText(), skipInputValidation: true);
                break;
            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;
            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;
            default:
                writer.WriteNullValue();
                break;
        }
    }

    private static TaskResult<NhAssistantAgentPackage> Unsupported()
    {
        return TaskResult<NhAssistantAgentPackage>.Failed(
            NhAssistantErrorCodes.PackageFormatUnsupported,
            "The agent package is not in a format this host reads.");
    }

    private static TaskResult<NhAssistantAgentPackage> TooLarge()
    {
        return TaskResult<NhAssistantAgentPackage>.Failed(
            NhAssistantErrorCodes.PackageTooLarge,
            "The agent package is larger than 1 MB or holds more than 20 agents.");
    }
}

/// <summary>
/// The strict JSON of the agent package format: camelCase names matched exactly, an unknown property
/// or a missing required one refused, numbers as numbers, <see langword="null"/> values left out.
/// </summary>
[JsonSourceGenerationOptions(
    JsonSerializerDefaults.Web,
    PropertyNameCaseInsensitive = false,
    NumberHandling = JsonNumberHandling.Strict,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    RespectNullableAnnotations = true,
    RespectRequiredConstructorParameters = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = true)]
[JsonSerializable(typeof(NhAssistantAgentPackage))]
[JsonSerializable(typeof(NhAssistantPackageAgentDefinition))]
[JsonSerializable(typeof(NhAssistantPackageRunSettings))]
[JsonSerializable(typeof(NhAssistantPackageSkill))]
[JsonSerializable(typeof(NhAssistantPackageTrigger))]
[JsonSerializable(typeof(NhAssistantPackageConsultation))]
[JsonSerializable(typeof(NhAssistantPackageHandOff))]
[JsonSerializable(typeof(JsonElement))]
public sealed partial class NhAssistantAgentPackageFormatJsonSerializerContext : JsonSerializerContext;
