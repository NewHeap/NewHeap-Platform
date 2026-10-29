using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Schema;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.AI.Test;
using NewHeap.Platform.Common.Models;
using Xunit;

namespace NewHeap.Platform.AI.Tests;

/// <summary>
/// FB43: a generated tool describes <see cref="DateOnly"/> and <see cref="TimeOnly"/> as the strings
/// System.Text.Json reads and writes, <c>"2026-11-01"</c> and <c>"14:30:00"</c>, with the formats
/// <c>date</c> and <c>time</c>, nullable and in lists as every other value type, and its function binds
/// exactly those strings.
/// </summary>
public sealed class NhAiToolDateSchemaTests
{
    private const string PlanSchema =
        "{\"type\":\"object\",\"properties\":{"
        + "\"due\":{\"type\":\"string\",\"format\":\"date\"},"
        + "\"end\":{\"anyOf\":[{\"type\":\"string\",\"format\":\"time\"},{\"type\":\"null\"}]},"
        + "\"followUp\":{\"anyOf\":[{\"type\":\"string\",\"format\":\"date\"},{\"type\":\"null\"}]},"
        + "\"holidays\":{\"type\":\"array\",\"items\":{\"type\":\"string\",\"format\":\"date\"}},"
        + "\"start\":{\"type\":\"string\",\"format\":\"time\"}},"
        + "\"required\":[\"due\",\"holidays\",\"start\"],"
        + "\"additionalProperties\":false}";

    [Fact]
    public void Date_only_and_time_only_are_strings_with_the_date_and_time_formats()
    {
        var descriptor = Assert.Single(new DateSchemaToolsNhAiCatalog().Descriptors);

        Assert.Equal(PlanSchema, descriptor.InputSchemaJson);
        Assert.Equal(PlanSchema, descriptor.OutputSchemaJson);
    }

    [Fact]
    public void A_serializer_context_keeps_the_date_and_time_formats()
    {
        var descriptor = Assert.Single(new WebDateSchemaToolsNhAiCatalog().Descriptors);

        Assert.Equal(PlanSchema, descriptor.InputSchemaJson);
    }

    [Fact]
    public void The_generator_writes_what_the_runtime_schema_exporters_write()
    {
        var descriptor = Assert.Single(new DateSchemaToolsNhAiCatalog().Descriptors);
        using var generated = JsonDocument.Parse(descriptor.InputSchemaJson);
        var properties = generated.RootElement.GetProperty("properties");
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            TypeInfoResolver = new DefaultJsonTypeInfoResolver()
        };

        // System.Text.Json's exporter and Microsoft.Extensions.AI, which hand-built and composed tools
        // use, describe both types the same way.
        foreach (var (name, type) in new[] { ("due", typeof(DateOnly)), ("start", typeof(TimeOnly)) })
        {
            var exported = options.GetJsonSchemaAsNode(type);
            var described = AIJsonUtilities.CreateJsonSchema(type);
            Assert.Equal(properties.GetProperty(name).GetProperty("type").GetString(), exported["type"]!.GetValue<string>());
            Assert.Equal(properties.GetProperty(name).GetProperty("format").GetString(), exported["format"]!.GetValue<string>());
            Assert.Equal(properties.GetProperty(name).GetProperty("format").GetString(), described.GetProperty("format").GetString());
        }
    }

    [Fact]
    public async Task The_function_binds_the_date_and_time_strings_and_writes_them_back_as_strings()
    {
        var tools = new DateSchemaTools();
        var function = CreateFunction(new DateSchemaToolsNhAiCatalog(), tools);

        var output = await function.InvokeAsync(new AIFunctionArguments
        {
            ["input"] = JsonSerializer.SerializeToElement(new
            {
                due = "2026-11-01",
                followUp = (string?)null,
                start = "14:30",
                end = "16:45:30.5",
                holidays = new[] { "2026-12-25" }
            })
        });
        var result = output is JsonElement element
            ? element
            : JsonSerializer.SerializeToElement(output, function.JsonSerializerOptions);

        Assert.True(result.GetProperty("success").GetBoolean(), result.GetRawText());
        var received = Assert.Single(tools.Received);
        Assert.Equal(new DateOnly(2026, 11, 1), received.Due);
        Assert.Null(received.FollowUp);
        Assert.Equal(new TimeOnly(14, 30), received.Start);
        Assert.Equal(new TimeOnly(16, 45, 30, 500), received.End);
        Assert.Equal([new DateOnly(2026, 12, 25)], received.Holidays);
        var data = result.GetProperty("data");
        Assert.Equal("2026-11-01", data.GetProperty("due").GetString());
        Assert.Equal("14:30:00", data.GetProperty("start").GetString());
        Assert.Equal("16:45:30.5000000", data.GetProperty("end").GetString());
        Assert.Equal("2026-12-25", data.GetProperty("holidays")[0].GetString());
    }

    [Fact]
    public async Task An_object_for_a_date_is_refused_as_input_the_tool_cannot_bind()
    {
        var tools = new DateSchemaTools();
        var function = CreateFunction(new DateSchemaToolsNhAiCatalog(), tools);

        var output = await function.InvokeAsync(new AIFunctionArguments
        {
            ["input"] = JsonSerializer.SerializeToElement(new
            {
                due = new { year = 2026, month = 11, day = 1 },
                start = "14:30:00",
                holidays = Array.Empty<string>()
            })
        });
        var result = output is JsonElement element
            ? element
            : JsonSerializer.SerializeToElement(output, function.JsonSerializerOptions);

        Assert.False(result.GetProperty("success").GetBoolean());
        Assert.Equal(NhAiToolFailureCodes.InputInvalid, result.GetProperty("code").GetString());
        Assert.Empty(tools.Received);
    }

    private static AIFunction CreateFunction(INhAiToolCatalog catalog, DateSchemaTools tools)
    {
        var services = new ServiceCollection();
        services.AddSingleton(tools);
        services.AddSingleton<INhAiToolInvocationGate>(NhAiTestInvocationGate.Authorized(
            new NhAiInvocationContext("actor-1", "test", new Dictionary<string, string>())));
        services.AddSingleton<INhAiBudgetManager>(new NhAiTestBudgetManager());
        services.AddNewHeapPlatformAI(ai => ai.AddGeneratedToolCatalog<DateSchemaToolsNhAiCatalog>());
        return Assert.Single(catalog.CreateFunctions(services.BuildServiceProvider()));
    }
}

/// <summary>A plan with a due date, an optional follow-up date, a start and an optional end time.</summary>
public sealed record DateSchemaPlan(
    DateOnly Due,
    DateOnly? FollowUp,
    TimeOnly Start,
    TimeOnly? End,
    IReadOnlyList<DateOnly> Holidays);

[NhAiToolSet("date-schema")]
public sealed class DateSchemaTools
{
    public List<DateSchemaPlan> Received { get; } = [];

    [NhAiTool("echo", 1, NhAiToolEffect.ReadOnly, NhAiToolExposure.Local)]
    [Description("Echo a plan with dates and times of day.")]
    public Task<TaskResult<DateSchemaPlan>> EchoAsync(
        DateSchemaPlan input,
        NhAiInvocationContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Received.Add(input);
        return Task.FromResult(TaskResult<DateSchemaPlan>.Succeeded(input));
    }
}

[NhAiToolSet("date-schema-web", JsonSerializerContextType = typeof(WebDateSchemaJsonContext))]
public sealed class WebDateSchemaTools
{
    [NhAiTool("echo", 1, NhAiToolEffect.ReadOnly, NhAiToolExposure.Local)]
    [Description("Echo a plan with dates and times of day with a web-default context.")]
    public Task<TaskResult<DateSchemaPlan>> EchoAsync(
        DateSchemaPlan input,
        NhAiInvocationContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(TaskResult<DateSchemaPlan>.Succeeded(input));
    }
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(DateSchemaPlan))]
internal partial class WebDateSchemaJsonContext : JsonSerializerContext
{
}
