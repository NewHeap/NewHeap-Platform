using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using NewHeap.Platform.AI.Mcp;
using NewHeap.Platform.Common.Utilities;
using Xunit;

namespace NewHeap.Platform.AI.Tests;

/// <summary>
/// The format and arguments a failed MCP tool result carries next to its message say exactly what
/// the text says: every argument the format renders, as a JSON primitive or as the text it shows,
/// within the bounds, and nothing when the format filled with them does not give the text.
/// </summary>
public sealed class NhAiMcpMessageFormatTests
{
    private static readonly JsonSerializerOptions Relaxed = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    [Fact]
    public void A_message_with_arguments_publishes_its_format_and_the_values_it_shows()
    {
        var project = "Roadmap";
        var openTasks = 3;
        FormattableString message = $"Project '{project}' has {openTasks} open tasks.";

        var created = NhAiMcpMessageFormat.TryCreate(message, Text(message), out var format, out var arguments);

        Assert.True(created);
        Assert.Equal("Project '{0}' has {1} open tasks.", format);
        Assert.Equal("""["Roadmap",3]""", arguments!.ToJsonString());
    }

    [Fact]
    public void Strings_booleans_and_numbers_are_json_primitives()
    {
        var name = "Roadmap";
        var active = true;
        byte small = 1;
        var negative = -2;
        var large = 3_000_000_000L;
        var largest = ulong.MaxValue;
        var ratio = 1.5d;
        var share = 0.25f;
        var amount = 12.50m;
        FormattableString message =
            $"{name} {active} {small} {negative} {large} {largest} {ratio} {share} {amount}";

        Assert.True(NhAiMcpMessageFormat.TryCreate(message, Text(message), out var format, out var arguments));

        Assert.Equal("{0} {1} {2} {3} {4} {5} {6} {7} {8}", format);
        Assert.Equal(
            """["Roadmap",true,1,-2,3000000000,18446744073709551615,1.5,0.25,12.50]""",
            arguments!.ToJsonString());
    }

    [Fact]
    public void Dates_are_iso_8601_to_the_second_shown_and_other_types_the_text_they_show()
    {
        var createdAt = new DateTime(2026, 9, 30, 8, 0, 5, 678, DateTimeKind.Utc);
        var closedAt = new DateTimeOffset(2026, 10, 1, 9, 30, 15, 123, TimeSpan.FromHours(2));
        var due = new DateOnly(2026, 11, 2);
        string? owner = null;
        var status = DayOfWeek.Friday;
        var id = Guid.Parse("0f8fad5b-d9cb-469f-a165-70867728950e");
        var duration = new TimeSpan(1, 2, 3);
        var grade = 'A';
        var unknown = double.NaN;
        var label = new ShownLabel("sprint 7");
        FormattableString message =
            $"{createdAt}|{closedAt}|{due}|{owner}|{status}|{id}|{duration}|{grade}|{unknown}|{label}";

        Assert.True(NhAiMcpMessageFormat.TryCreate(message, Text(message), out _, out var arguments));

        // The text shows 09/30/2026 08:00:05 and 10/01/2026 09:30:15 +02:00: no milliseconds.
        Assert.Equal(
            """["2026-09-30T08:00:05","2026-10-01T09:30:15+02:00","2026-11-02",null,"Friday","0f8fad5b-d9cb-469f-a165-70867728950e","01:02:03","A","NaN","label sprint 7"]""",
            arguments!.ToJsonString(Relaxed));
    }

    [Fact]
    public void An_argument_with_a_specifier_or_an_alignment_is_the_text_it_shows()
    {
        var amount = 1234.5m;
        var due = new DateTimeOffset(2026, 11, 2, 23, 59, 0, TimeSpan.Zero);
        var code = "ab";
        FormattableString message = $"Pay {amount:N2} before {due:yyyy-MM-dd} [{code,4}].";

        Assert.True(NhAiMcpMessageFormat.TryCreate(message, Text(message), out var format, out var arguments));

        Assert.Equal("Pay {0:N2} before {1:yyyy-MM-dd} [{2,4}].", format);
        // The time of day the text leaves out stays out.
        Assert.Equal("""["1,234.50","2026-11-02","ab"]""", arguments!.ToJsonString());
        Assert.Equal("Pay 1,234.50 before 2026-11-02 [  ab].", Text(message));
    }

    [Fact]
    public void A_fixed_message_is_its_own_format_without_arguments_even_with_braces()
    {
        const string text = "Send the arguments as {\"input\": {...}}.";

        Assert.True(NhAiMcpMessageFormat.TryCreate(
            NhAiMcpMessageFormat.Fixed(text),
            text,
            out var format,
            out var arguments));

        Assert.Equal("Send the arguments as {{\"input\": {{...}}}}.", format);
        Assert.Equal("[]", arguments!.ToJsonString());
        Assert.Equal(text, string.Format(CultureInfo.InvariantCulture, format));
    }

    [Fact]
    public void A_plain_text_message_is_its_own_format_unless_its_braces_make_it_no_composite_format()
    {
        var plain = SafeFormattableStringFactory.Create("The engine returned no receipt.");
        var braces = SafeFormattableStringFactory.Create("Send the arguments as {\"input\": {...}}.");

        Assert.True(NhAiMcpMessageFormat.TryCreate(plain, plain.ToString(), out var format, out var arguments));
        Assert.Equal("The engine returned no receipt.", format);
        Assert.Equal("[]", arguments!.ToJsonString());
        Assert.False(NhAiMcpMessageFormat.TryCreate(braces, braces.ToString(), out _, out _));
    }

    [Fact]
    public void An_argument_the_text_does_not_show_publishes_nothing()
    {
        var unshown = FormattableStringFactory.Create("The project is closed.", "secret-owner");
        var outOfRange = FormattableStringFactory.Create("Project {1} is closed.", "secret-owner");

        Assert.False(NhAiMcpMessageFormat.TryCreate(unshown, "The project is closed.", out _, out _));
        Assert.False(NhAiMcpMessageFormat.TryCreate(outOfRange, "Project  is closed.", out _, out _));
    }

    [Fact]
    public void An_argument_shown_twice_needs_one_specifier()
    {
        var same = FormattableStringFactory.Create("{0} or {0}", "draft");
        var different = FormattableStringFactory.Create("{0:N0} or {0:N2}", 1.5m);

        Assert.True(NhAiMcpMessageFormat.TryCreate(same, "draft or draft", out _, out var arguments));
        Assert.Equal("""["draft"]""", arguments!.ToJsonString());
        Assert.False(NhAiMcpMessageFormat.TryCreate(different, Text(different), out _, out _));
    }

    [Fact]
    public void A_text_the_arguments_do_not_render_exactly_publishes_nothing()
    {
        var count = 1;
        FormattableString message = $"Engine {count} is busy.";
        var previous = CultureInfo.CurrentCulture;
        try
        {
            // A safe formattable string renders with the current culture; the published arguments use
            // the invariant one, so a text in another culture's notation gets no format.
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("nl-NL");
            var local = SafeFormattableStringFactory.Create("Ratio {0}.", 1.5);

            Assert.Equal("Ratio 1,5.", local.ToString());
            Assert.False(NhAiMcpMessageFormat.TryCreate(local, local.ToString(), out _, out _));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }

        Assert.False(NhAiMcpMessageFormat.TryCreate(message, "Engine 2 is busy.", out _, out _));
        Assert.False(NhAiMcpMessageFormat.TryCreate(message, " ", out _, out _));
        Assert.False(NhAiMcpMessageFormat.TryCreate(null, "Engine 1 is busy.", out _, out _));
    }

    [Fact]
    public void An_argument_that_cannot_render_publishes_nothing()
    {
        var message = SafeFormattableStringFactory.Create("Owner {0}.", new UnrenderableLabel());

        Assert.False(NhAiMcpMessageFormat.TryCreate(message, message.ToString(), out _, out _));
    }

    [Fact]
    public void The_format_and_arguments_stay_within_their_bounds()
    {
        var atArgumentCount = Many(NhAiMcpResultMetadata.MaxMessageArguments);
        var overArgumentCount = Many(NhAiMcpResultMetadata.MaxMessageArguments + 1);
        var atArgumentLength = FormattableStringFactory.Create(
            "Name {0}.",
            new string('n', NhAiMcpResultMetadata.MaxMessageArgumentLength));
        var overArgumentLength = FormattableStringFactory.Create(
            "Name {0}.",
            new string('n', NhAiMcpResultMetadata.MaxMessageArgumentLength + 1));
        var atFormatLength = FormattableStringFactory.Create(
            new string('f', NhAiMcpResultMetadata.MaxMessageFormatLength));
        var overFormatLength = FormattableStringFactory.Create(
            new string('f', NhAiMcpResultMetadata.MaxMessageFormatLength + 1));

        Assert.Equal(16, NhAiMcpResultMetadata.MaxMessageArguments);
        Assert.Equal(256, NhAiMcpResultMetadata.MaxMessageArgumentLength);
        Assert.Equal(1_024, NhAiMcpResultMetadata.MaxMessageFormatLength);
        Assert.True(NhAiMcpMessageFormat.TryCreate(atArgumentCount, Text(atArgumentCount), out _, out var arguments));
        Assert.Equal(NhAiMcpResultMetadata.MaxMessageArguments, arguments!.Count);
        Assert.False(NhAiMcpMessageFormat.TryCreate(overArgumentCount, Text(overArgumentCount), out _, out _));
        Assert.True(NhAiMcpMessageFormat.TryCreate(atArgumentLength, Text(atArgumentLength), out _, out _));
        Assert.False(NhAiMcpMessageFormat.TryCreate(overArgumentLength, Text(overArgumentLength), out _, out _));
        Assert.True(NhAiMcpMessageFormat.TryCreate(atFormatLength, Text(atFormatLength), out _, out _));
        Assert.False(NhAiMcpMessageFormat.TryCreate(overFormatLength, Text(overFormatLength), out _, out _));
    }

    [Fact]
    public void The_client_reads_the_format_with_its_arguments_or_neither()
    {
        var published = NhAiMcpToolFailureDetail.From(Failure(new JsonObject
        {
            [NhAiMcpResultMetadata.MessageFormatKey] = "Project '{0}' has {1} open tasks.",
            [NhAiMcpResultMetadata.MessageArgumentsKey] = new JsonArray("Roadmap", 3, null)
        }));
        var withoutArguments = NhAiMcpToolFailureDetail.From(Failure(new JsonObject
        {
            [NhAiMcpResultMetadata.MessageFormatKey] = "Project '{0}' is closed."
        }));
        var notAnArray = NhAiMcpToolFailureDetail.From(Failure(new JsonObject
        {
            [NhAiMcpResultMetadata.MessageFormatKey] = "Project '{0}' is closed.",
            [NhAiMcpResultMetadata.MessageArgumentsKey] = "Roadmap"
        }));
        var nested = NhAiMcpToolFailureDetail.From(Failure(new JsonObject
        {
            [NhAiMcpResultMetadata.MessageFormatKey] = "Project '{0}' is closed.",
            [NhAiMcpResultMetadata.MessageArgumentsKey] = new JsonArray(new JsonObject { ["name"] = "Roadmap" })
        }));
        var tooMany = NhAiMcpToolFailureDetail.From(Failure(new JsonObject
        {
            [NhAiMcpResultMetadata.MessageFormatKey] = "Too many.",
            [NhAiMcpResultMetadata.MessageArgumentsKey] = new JsonArray(Enumerable
                .Range(0, NhAiMcpResultMetadata.MaxMessageArguments + 1)
                .Select(index => (JsonNode?)JsonValue.Create(index))
                .ToArray())
        }));

        Assert.Equal("Project '{0}' has {1} open tasks.", published.MessageFormat);
        Assert.Equal(
            new[] { "\"Roadmap\"", "3", "null" },
            published.MessageArguments.Select(argument => argument.GetRawText()).ToArray());
        Assert.Equal(JsonValueKind.Null, published.MessageArguments[2].ValueKind);
        foreach (var unread in new[] { withoutArguments, notAnArray, nested, tooMany })
        {
            Assert.Null(unread.MessageFormat);
            Assert.Empty(unread.MessageArguments);
            Assert.Equal("project-closed", unread.Code);
        }
    }

    private static string Text(FormattableString message)
    {
        // The shared tool result renders every message with the invariant culture.
        return message.ToString(CultureInfo.InvariantCulture);
    }

    private static FormattableString Many(int count)
    {
        var format = string.Join(" ", Enumerable.Range(0, count).Select(index => "{" + index + "}"));
        return FormattableStringFactory.Create(
            format,
            Enumerable.Range(0, count).Select(index => (object?)index).ToArray());
    }

    private static CallToolResult Failure(JsonObject meta)
    {
        meta[NhAiMcpResultMetadata.CodeKey] = "project-closed";
        return new CallToolResult
        {
            IsError = true,
            Content = [new TextContentBlock { Text = "project-closed: The project is closed." }],
            Meta = meta
        };
    }

    private sealed record ShownLabel(string Name)
    {
        public override string ToString()
        {
            return "label " + Name;
        }
    }

    private sealed class UnrenderableLabel
    {
        public override string ToString()
        {
            throw new InvalidOperationException("The label cannot render.");
        }
    }
}
