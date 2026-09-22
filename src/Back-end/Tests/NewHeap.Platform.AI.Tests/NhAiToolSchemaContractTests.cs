using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;
using NewHeap.Platform.AI.Test;
using NewHeap.Platform.Common.Models;
using Xunit;

namespace NewHeap.Platform.AI.Tests;

public sealed class NhAiToolSchemaContractTests
{
    [Fact]
    public void Output_schema_snapshot_follows_each_json_ignore_condition()
    {
        var descriptor = Assert.Single(new DefaultSchemaToolsNhAiCatalog().Descriptors);

        Assert.Equal(
            "{\"type\":\"object\",\"properties\":{"
            + "\"alwaysWritten\":{\"anyOf\":[{\"type\":\"string\"},{\"type\":\"null\"}]},"
            + "\"color\":{\"type\":\"string\",\"enum\":[\"Red\",\"DeepBlue\"]},"
            + "\"count\":{\"type\":\"integer\"},"
            + "\"explicit_name\":{\"type\":\"string\"},"
            + "\"httpServer2Go\":{\"type\":\"string\"},"
            + "\"optional\":{\"anyOf\":[{\"type\":\"string\"},{\"type\":\"null\"}]},"
            + "\"optionalNumber\":{\"anyOf\":[{\"type\":\"integer\"},{\"type\":\"null\"}]},"
            + "\"skippedWhenDefault\":{\"type\":\"integer\"},"
            + "\"skippedWhenNull\":{\"anyOf\":[{\"type\":\"string\"},{\"type\":\"null\"}]},"
            + "\"urlName\":{\"type\":\"string\"},"
            + "\"writeOnlyOutput\":{\"anyOf\":[{\"type\":\"string\"},{\"type\":\"null\"}]}},"
            + "\"required\":[\"alwaysWritten\",\"color\",\"count\",\"explicit_name\",\"httpServer2Go\",\"urlName\",\"writeOnlyOutput\"],"
            + "\"additionalProperties\":false}",
            descriptor.OutputSchemaJson);
    }

    [Fact]
    public void Input_schema_snapshot_follows_each_json_ignore_condition()
    {
        var descriptor = Assert.Single(new DefaultSchemaToolsNhAiCatalog().Descriptors);

        Assert.Equal(
            "{\"type\":\"object\",\"properties\":{"
            + "\"alwaysWritten\":{\"anyOf\":[{\"type\":\"string\"},{\"type\":\"null\"}]},"
            + "\"color\":{\"type\":\"string\",\"enum\":[\"Red\",\"DeepBlue\"]},"
            + "\"count\":{\"type\":\"integer\"},"
            + "\"explicit_name\":{\"type\":\"string\"},"
            + "\"httpServer2Go\":{\"type\":\"string\"},"
            + "\"optional\":{\"anyOf\":[{\"type\":\"string\"},{\"type\":\"null\"}]},"
            + "\"optionalNumber\":{\"anyOf\":[{\"type\":\"integer\"},{\"type\":\"null\"}]},"
            + "\"readOnlyInput\":{\"anyOf\":[{\"type\":\"string\"},{\"type\":\"null\"}]},"
            + "\"skippedWhenDefault\":{\"type\":\"integer\"},"
            + "\"skippedWhenNull\":{\"anyOf\":[{\"type\":\"string\"},{\"type\":\"null\"}]},"
            + "\"urlName\":{\"type\":\"string\"}},"
            + "\"required\":[\"color\",\"count\",\"explicit_name\",\"httpServer2Go\",\"skippedWhenDefault\",\"urlName\"],"
            + "\"additionalProperties\":false}",
            descriptor.InputSchemaJson);
    }

    public static TheoryData<string> SerializerContracts => new()
    {
        "default",
        "flat-default",
        "web-context",
        "general-context",
        "snake-lower-context",
        "kebab-upper-context"
    };

    [Theory]
    [MemberData(nameof(SerializerContracts))]
    public void Output_schema_describes_exactly_what_the_runtime_serializer_writes(string contract)
    {
        var (descriptor, options) = CreateContract(contract);
        using var schema = JsonDocument.Parse(descriptor.OutputSchemaJson);
        var schemaProperties = schema.RootElement.GetProperty("properties")
            .EnumerateObject()
            .Select(property => property.Name)
            .ToHashSet(StringComparer.Ordinal);
        var required = schema.RootElement.TryGetProperty("required", out var requiredElement)
            ? requiredElement.EnumerateArray().Select(item => item.GetString()!).ToArray()
            : [];

        using var populated = JsonDocument.Parse(JsonSerializer.Serialize(Populated(), options));
        using var empty = JsonDocument.Parse(JsonSerializer.Serialize(new SchemaContractModel(), options));
        var populatedNames = populated.RootElement.EnumerateObject()
            .Select(property => property.Name)
            .ToHashSet(StringComparer.Ordinal);
        var emptyNames = empty.RootElement.EnumerateObject()
            .Select(property => property.Name)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Equal(schemaProperties.Order(StringComparer.Ordinal), populatedNames.Order(StringComparer.Ordinal));
        Assert.Subset(schemaProperties, emptyNames);
        Assert.Subset(emptyNames, required.ToHashSet(StringComparer.Ordinal));

        var colorName = schemaProperties.Single(name =>
            name.Equals("color", StringComparison.OrdinalIgnoreCase));
        var colorSchema = schema.RootElement.GetProperty("properties").GetProperty(colorName);
        var writtenColor = populated.RootElement.GetProperty(colorName);
        if (colorSchema.GetProperty("type").GetString() == "string")
        {
            Assert.Contains(
                writtenColor.GetString(),
                colorSchema.GetProperty("enum").EnumerateArray().Select(item => item.GetString()));
        }
        else
        {
            Assert.Equal(JsonValueKind.Number, writtenColor.ValueKind);
        }
    }

    [Fact]
    public void Naming_policies_match_system_text_json_for_acronyms_and_digits()
    {
        var names = new Dictionary<string, (NhAiToolDescriptor Descriptor, JsonNamingPolicy? Policy)>
        {
            ["default"] = (CreateContract("default").Descriptor, JsonNamingPolicy.CamelCase),
            ["snake"] = (CreateContract("snake-lower-context").Descriptor, JsonNamingPolicy.SnakeCaseLower),
            ["kebab"] = (CreateContract("kebab-upper-context").Descriptor, JsonNamingPolicy.KebabCaseUpper),
            ["general"] = (CreateContract("general-context").Descriptor, null)
        };

        foreach (var (_, (descriptor, policy)) in names)
        {
            using var schema = JsonDocument.Parse(descriptor.OutputSchemaJson);
            var properties = schema.RootElement.GetProperty("properties");
            foreach (var clrName in new[] { "URLName", "HTTPServer2Go", "OptionalNumber" })
            {
                var expected = policy?.ConvertName(clrName) ?? clrName;
                Assert.True(
                    properties.TryGetProperty(expected, out _),
                    $"Expected '{expected}' in {descriptor.CatalogId} schema {descriptor.OutputSchemaJson}");
            }
        }
    }

    private static SchemaContractModel Populated()
    {
        return new SchemaContractModel
        {
            URLName = "url",
            Count = 3,
            Optional = "optional",
            OptionalNumber = 7,
            Color = SchemaColor.DeepBlue,
            AlwaysWritten = "always",
            Hidden = "hidden",
            SkippedWhenDefault = 5,
            SkippedWhenNull = "present",
            ReadOnlyInput = "input-only",
            WriteOnlyOutput = "output-only",
            ExplicitName = "explicit",
            HTTPServer2Go = "server"
        };
    }

    private static (NhAiToolDescriptor Descriptor, JsonSerializerOptions Options) CreateContract(string contract)
    {
        INhAiToolCatalog catalog = contract switch
        {
            "default" => new DefaultSchemaToolsNhAiCatalog(),
            "flat-default" => new FlatDefaultSchemaToolsNhAiCatalog(),
            "web-context" => new WebSchemaToolsNhAiCatalog(),
            "general-context" => new GeneralSchemaToolsNhAiCatalog(),
            "snake-lower-context" => new SnakeSchemaToolsNhAiCatalog(),
            "kebab-upper-context" => new KebabSchemaToolsNhAiCatalog(),
            _ => throw new ArgumentOutOfRangeException(nameof(contract))
        };
        var provider = new SchemaServiceProvider(
            new DefaultSchemaTools(),
            new FlatDefaultSchemaTools(),
            new WebSchemaTools(),
            new GeneralSchemaTools(),
            new SnakeSchemaTools(),
            new KebabSchemaTools(),
            new NhAiToolInvoker(
                NhAiTestInvocationGate.Authorized(
                    new NhAiInvocationContext("actor-1", "test", new Dictionary<string, string>())),
                new NhAiTestBudgetManager()));
        var function = Assert.Single(catalog.CreateFunctions(provider));
        return (Assert.Single(catalog.Descriptors), function.JsonSerializerOptions);
    }

    private sealed class SchemaServiceProvider(params object[] services) : IServiceProvider
    {
        public object? GetService(Type serviceType)
        {
            return services.FirstOrDefault(serviceType.IsInstanceOfType);
        }
    }
}

public enum SchemaColor
{
    Red,
    DeepBlue
}

public sealed class SchemaContractModel
{
    public string URLName { get; set; } = string.Empty;

    public int Count { get; set; }

    public string? Optional { get; set; }

    public int? OptionalNumber { get; set; }

    public SchemaColor Color { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? AlwaysWritten { get; set; }

    [JsonIgnore]
    public string? Hidden { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int SkippedWhenDefault { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SkippedWhenNull { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWriting)]
    public string? ReadOnlyInput { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenReading)]
    public string? WriteOnlyOutput { get; set; }

    [JsonPropertyName("explicit_name")]
    public string ExplicitName { get; set; } = string.Empty;

    public string HTTPServer2Go { get; set; } = string.Empty;
}

internal static class SchemaToolBody
{
    public static Task<TaskResult<SchemaContractModel>> EchoAsync(
        SchemaContractModel input,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(TaskResult<SchemaContractModel>.Succeeded(input));
    }
}

[NhAiToolSet("schema-default")]
public sealed class DefaultSchemaTools
{
    [NhAiTool("echo", 1, NhAiToolEffect.ReadOnly, NhAiToolExposure.Local)]
    [Description("Echo the schema contract model with the Microsoft.Extensions.AI defaults.")]
    public Task<TaskResult<SchemaContractModel>> EchoAsync(
        SchemaContractModel input,
        NhAiInvocationContext context,
        CancellationToken cancellationToken)
    {
        return SchemaToolBody.EchoAsync(input, cancellationToken);
    }
}

[NhAiToolSet("schema-flat-default")]
public sealed class FlatDefaultSchemaTools
{
    [NhAiTool(
        "echo",
        1,
        NhAiToolEffect.ReadOnly,
        NhAiToolExposure.Local,
        ExportSchema = NhAiToolExportSchema.Flat)]
    [Description("Echo the schema contract model with the flat export defaults.")]
    public Task<TaskResult<SchemaContractModel>> EchoAsync(
        SchemaContractModel input,
        NhAiInvocationContext context,
        CancellationToken cancellationToken)
    {
        return SchemaToolBody.EchoAsync(input, cancellationToken);
    }
}

[NhAiToolSet("schema-web", JsonSerializerContextType = typeof(WebSchemaJsonContext))]
public sealed class WebSchemaTools
{
    [NhAiTool("echo", 1, NhAiToolEffect.ReadOnly, NhAiToolExposure.Local)]
    [Description("Echo the schema contract model with a web-default context.")]
    public Task<TaskResult<SchemaContractModel>> EchoAsync(
        SchemaContractModel input,
        NhAiInvocationContext context,
        CancellationToken cancellationToken)
    {
        return SchemaToolBody.EchoAsync(input, cancellationToken);
    }
}

[NhAiToolSet("schema-general", JsonSerializerContextType = typeof(GeneralSchemaJsonContext))]
public sealed class GeneralSchemaTools
{
    [NhAiTool("echo", 1, NhAiToolEffect.ReadOnly, NhAiToolExposure.Local)]
    [Description("Echo the schema contract model with a context that declares no options.")]
    public Task<TaskResult<SchemaContractModel>> EchoAsync(
        SchemaContractModel input,
        NhAiInvocationContext context,
        CancellationToken cancellationToken)
    {
        return SchemaToolBody.EchoAsync(input, cancellationToken);
    }
}

[NhAiToolSet("schema-snake", JsonSerializerContextType = typeof(SnakeSchemaJsonContext))]
public sealed class SnakeSchemaTools
{
    [NhAiTool("echo", 1, NhAiToolEffect.ReadOnly, NhAiToolExposure.Local)]
    [Description("Echo the schema contract model with a snake_case context.")]
    public Task<TaskResult<SchemaContractModel>> EchoAsync(
        SchemaContractModel input,
        NhAiInvocationContext context,
        CancellationToken cancellationToken)
    {
        return SchemaToolBody.EchoAsync(input, cancellationToken);
    }
}

[NhAiToolSet("schema-kebab", JsonSerializerContextType = typeof(KebabSchemaJsonContext))]
public sealed class KebabSchemaTools
{
    [NhAiTool("echo", 1, NhAiToolEffect.ReadOnly, NhAiToolExposure.Local)]
    [Description("Echo the schema contract model with an upper kebab-case context.")]
    public Task<TaskResult<SchemaContractModel>> EchoAsync(
        SchemaContractModel input,
        NhAiInvocationContext context,
        CancellationToken cancellationToken)
    {
        return SchemaToolBody.EchoAsync(input, cancellationToken);
    }
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(SchemaContractModel))]
[JsonSerializable(typeof(TaskResult<SchemaContractModel>))]
internal partial class WebSchemaJsonContext : JsonSerializerContext
{
}

[JsonSerializable(typeof(SchemaContractModel))]
[JsonSerializable(typeof(TaskResult<SchemaContractModel>))]
internal partial class GeneralSchemaJsonContext : JsonSerializerContext
{
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(SchemaContractModel))]
[JsonSerializable(typeof(TaskResult<SchemaContractModel>))]
internal partial class SnakeSchemaJsonContext : JsonSerializerContext
{
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.KebabCaseUpper,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault)]
[JsonSerializable(typeof(SchemaContractModel))]
[JsonSerializable(typeof(TaskResult<SchemaContractModel>))]
internal partial class KebabSchemaJsonContext : JsonSerializerContext
{
}
