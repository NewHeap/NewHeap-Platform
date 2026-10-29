using System.ComponentModel;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.AI.Test;
using NewHeap.Platform.Common.Models;
using Xunit;

namespace NewHeap.Platform.AI.Scripting.Tests;

/// <summary>
/// FB43: a script sees a <see cref="DateOnly"/> as <c>string /* date */</c> and a <see cref="TimeOnly"/>
/// as <c>string /* time */</c>, its call check accepts exactly the strings System.Text.Json reads for
/// them, and a generated tool receives the date and time a script passed.
/// </summary>
public sealed class NhAiScriptDateTests
{
    private const string PlanSchema = """
        {"type":"object","properties":{"due":{"type":"string","format":"date"},"end":{"anyOf":[{"type":"string","format":"time"},{"type":"null"}]},"start":{"type":"string","format":"time"}},"required":["due","start"],"additionalProperties":false}
        """;

    [Fact]
    public void The_signature_shows_a_date_and_a_time_as_strings_with_their_format()
    {
        var tool = new NhAiScriptToolOffer(
            "plans_echo_v1",
            FakeScriptHost.Descriptor("plans.echo", "plans_echo_v1", NhAiToolEffect.ReadOnly, PlanSchema, PlanSchema));

        Assert.Equal(
            "plans_echo_v1(input: { due: string /* date */; end?: string /* time */ | null; start: string /* time */ }): "
            + "Promise<Result<{ due: string /* date */; end?: string /* time */ | null; start: string /* time */ }>>;",
            NhAiScriptSignatures.Signature(tool));
    }

    public static TheoryData<string> ValidDates => new()
    {
        "2026-11-01",
        "2024-02-29",
        "0001-01-01",
        "9999-12-31"
    };

    public static TheoryData<string> InvalidDates => new()
    {
        "2026-11-1",
        "2026-13-01",
        "2025-02-29",
        "01-11-2026",
        "2026/11/01",
        "20261101",
        "2026-11-01T00:00:00",
        "2026-11-01Z",
        " 2026-11-01",
        ""
    };

    public static TheoryData<string> ValidTimes => new()
    {
        "14:30",
        "14:30:00",
        "00:00:00",
        "23:59:59.9999999",
        "08:05:09.5",
        "16:45:30.123"
    };

    public static TheoryData<string> InvalidTimes => new()
    {
        "24:00",
        "14:60",
        "14:30:60",
        "9:30",
        "14",
        "1430",
        "14:30:00Z",
        "14:30:00+01:00",
        "2:30 PM",
        "14:30:00.12345678",
        "1.14:30:00",
        "2026-11-01T14:30:00",
        ""
    };

    [Theory]
    [MemberData(nameof(ValidDates))]
    public void A_date_system_text_json_reads_passes_the_call_check(string date)
    {
        Assert.Null(NhAiScriptSignatures.Validate(Plan(), Json(new { due = date, start = "09:00" })));
        Assert.Equal(DateOnly.ParseExact(date, "yyyy-MM-dd"), JsonSerializer.Deserialize<DateOnly>(JsonSerializer.Serialize(date)));
    }

    [Theory]
    [MemberData(nameof(InvalidDates))]
    public void Any_other_date_string_stops_at_the_field_without_its_value(string date)
    {
        var error = NhAiScriptSignatures.Validate(Plan(), Json(new { due = date, start = "09:00" }));

        Assert.Equal(new NhAiScriptFieldError("due", "expected a date string yyyy-MM-dd"), error);
    }

    [Theory]
    [MemberData(nameof(ValidTimes))]
    public void A_time_system_text_json_reads_passes_the_call_check(string time)
    {
        Assert.Null(NhAiScriptSignatures.Validate(Plan(), Json(new { due = "2026-11-01", start = time, end = time })));
        Assert.Equal(TimeOnly.Parse(time, System.Globalization.CultureInfo.InvariantCulture), JsonSerializer.Deserialize<TimeOnly>(JsonSerializer.Serialize(time)));
    }

    [Theory]
    [MemberData(nameof(InvalidTimes))]
    public void Any_other_time_string_stops_at_the_field_without_its_value(string time)
    {
        var start = NhAiScriptSignatures.Validate(Plan(), Json(new { due = "2026-11-01", start = time }));
        var end = NhAiScriptSignatures.Validate(Plan(), Json(new { due = "2026-11-01", start = "09:00", end = time }));

        Assert.Equal(new NhAiScriptFieldError("start", "expected a time string HH:mm[:ss[.fffffff]]"), start);
        Assert.Equal(new NhAiScriptFieldError("end", "expected a time string HH:mm[:ss[.fffffff]]"), end);
    }

    [Fact]
    public void A_null_time_passes_where_the_time_is_nullable_and_an_object_never_passes_for_a_date()
    {
        Assert.Null(NhAiScriptSignatures.Validate(Plan(), Json(new { due = "2026-11-01", start = "09:00", end = (string?)null })));
        Assert.Equal(
            new NhAiScriptFieldError("due", "expected string /* date */, got object"),
            NhAiScriptSignatures.Validate(Plan(), Json(new { due = new { year = 2026, month = 11, day = 1 }, start = "09:00" })));
    }

    [Fact]
    public async Task A_generated_tool_receives_the_date_and_time_a_script_passed()
    {
        await using var host = GeneratedPlanHost.Create();

        var result = await RunAsync(
            "const plan = await tools." + host.Name + """
            ({ due: "2026-11-01", start: "14:30", end: null, holidays: ["2026-12-25"] });
            if (!plan.success) { return plan.code; }
            return { due: plan.data.due, start: plan.data.start, holidays: plan.data.holidays };
            """,
            host);

        Assert.True(result.Completed, result.Describe());
        var received = Assert.Single(host.Plans.Received);
        Assert.Equal(new DateOnly(2026, 11, 1), received.Due);
        Assert.Equal(new TimeOnly(14, 30), received.Start);
        Assert.Null(received.End);
        Assert.Equal([new DateOnly(2026, 12, 25)], received.Holidays);
        Assert.Equal("""{"due":"2026-11-01","start":"14:30:00","holidays":["2026-12-25"]}""", result.Value!.Value.GetRawText());
    }

    [Fact]
    public async Task A_date_the_tool_cannot_read_stops_the_script_before_the_tool_runs()
    {
        await using var host = GeneratedPlanHost.Create();

        var result = await RunAsync(
            "return await tools." + host.Name + """({ due: "1 November 2026", start: "14:30", holidays: [] });""",
            host);

        Assert.Equal(NhAiScriptCodes.CallInvalid, result.Stop?.Code);
        Assert.Equal(new NhAiScriptFieldError("due", "expected a date string yyyy-MM-dd"), result.Stop!.FieldError);
        Assert.Contains("due: string /* date */", result.Stop.Signature, StringComparison.Ordinal);
        Assert.Contains("start: string /* time */", result.Stop.Signature, StringComparison.Ordinal);
        Assert.Empty(host.Plans.Received);
    }

    private static NhAiScriptToolOffer Plan()
    {
        return new NhAiScriptToolOffer(
            "plans_echo_v1",
            FakeScriptHost.Descriptor("plans.echo", "plans_echo_v1", NhAiToolEffect.ReadOnly, PlanSchema, "{}"));
    }

    private static JsonElement Json(object value)
    {
        return JsonSerializer.SerializeToElement(value);
    }

    private static Task<NhAiScriptResult> RunAsync(string script, INhAiScriptHost host)
    {
        return new NhAiScriptRunner(new NhAiScriptingOptions()).RunAsync(script, host, new NhAiScriptLimits());
    }

    /// <summary>
    /// A host whose one tool is a generated catalog's governed function: a call runs through the
    /// function's own argument binding, as a turn's call does.
    /// </summary>
    private sealed class GeneratedPlanHost : INhAiScriptHost, IAsyncDisposable
    {
        private readonly ServiceProvider _provider;
        private readonly AIFunction _function;

        private GeneratedPlanHost(ServiceProvider provider)
        {
            _provider = provider;
            var catalog = new ScriptPlanToolsNhAiCatalog();
            _function = Assert.Single(catalog.CreateFunctions(provider));
            Plans = provider.GetRequiredService<ScriptPlanTools>();
            Tools = [new NhAiScriptToolOffer(_function.Name, Assert.Single(catalog.Descriptors))];
        }

        public string Name => _function.Name;

        public ScriptPlanTools Plans { get; }

        public IReadOnlyList<NhAiScriptToolOffer> Tools { get; }

        public static GeneratedPlanHost Create()
        {
            var services = new ServiceCollection();
            services.AddSingleton<ScriptPlanTools>();
            services.AddSingleton<INhAiToolInvocationGate>(NhAiTestInvocationGate.Authorized(
                new NhAiInvocationContext("actor-1", "script", new Dictionary<string, string>())));
            services.AddSingleton<INhAiBudgetManager>(new NhAiTestBudgetManager());
            services.AddNewHeapPlatformAI(ai => ai.AddGeneratedToolCatalog<ScriptPlanToolsNhAiCatalog>());
            return new GeneratedPlanHost(services.BuildServiceProvider());
        }

        public async ValueTask<NhAiScriptCallOutcome> CallAsync(NhAiScriptToolCall call, CancellationToken cancellationToken)
        {
            var output = await _function.InvokeAsync(new AIFunctionArguments { ["input"] = call.Arguments }, cancellationToken);
            var result = output is JsonElement element
                ? element
                : JsonSerializer.SerializeToElement(output, _function.JsonSerializerOptions);
            return NhAiScriptCallOutcome.Completed(result);
        }

        public ValueTask DisposeAsync()
        {
            return _provider.DisposeAsync();
        }
    }
}

/// <summary>A plan with a due date, a start and an optional end time and the holidays to skip.</summary>
public sealed record ScriptPlan(DateOnly Due, TimeOnly Start, TimeOnly? End, IReadOnlyList<DateOnly> Holidays);

[NhAiToolSet("script-plans")]
public sealed class ScriptPlanTools
{
    public List<ScriptPlan> Received { get; } = [];

    [NhAiTool("echo", 1, NhAiToolEffect.ReadOnly, NhAiToolExposure.Local)]
    [Description("Echo a plan with a due date and times of day.")]
    public Task<TaskResult<ScriptPlan>> EchoAsync(
        ScriptPlan input,
        NhAiInvocationContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (Received)
        {
            Received.Add(input);
        }

        return Task.FromResult(TaskResult<ScriptPlan>.Succeeded(input));
    }
}
