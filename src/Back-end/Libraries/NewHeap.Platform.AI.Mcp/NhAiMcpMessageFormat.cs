using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;

namespace NewHeap.Platform.AI.Mcp;

/// <summary>
/// Builds the format and arguments a failed MCP tool result carries next to its message
/// (<see cref="NhAiMcpResultMetadata.MessageFormatKey"/> and
/// <see cref="NhAiMcpResultMetadata.MessageArgumentsKey"/>). Both are published together or not at
/// all, and only when they say exactly what the text says: the format renders every argument, the
/// bounds hold, and the format filled with what each argument rendered gives the text character for
/// character.
/// </summary>
/// <remarks>
/// An argument becomes a JSON primitive: a string as a string, a boolean as a boolean, an integer
/// or finite floating-point number as a number, a <see cref="DateTime"/> as ISO 8601 to the second
/// (<c>yyyy-MM-ddTHH:mm:ss</c>), a <see cref="DateTimeOffset"/> with its offset as well and a
/// <see cref="DateOnly"/> as <c>yyyy-MM-dd</c>, all with the invariant culture, and null as
/// <c>null</c>. An argument the format gives a specifier, such as <c>{0:N2}</c> or
/// <c>{0:yyyy-MM-dd}</c>, and an argument of any other type become the string the text shows for
/// it.
/// </remarks>
internal static class NhAiMcpMessageFormat
{
    /// <summary>
    /// Builds the format and arguments of <paramref name="source"/>, the one message whose rendering
    /// is <paramref name="text"/>. Returns <see langword="false"/> when they cannot say exactly what
    /// the text says.
    /// </summary>
    public static bool TryCreate(
        FormattableString? source,
        string? text,
        [NotNullWhen(true)] out string? format,
        [NotNullWhen(true)] out JsonArray? arguments)
    {
        format = null;
        arguments = null;
        if (source is null || string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var candidate = source.Format;
        var values = source.GetArguments() ?? [];
        if (string.IsNullOrWhiteSpace(candidate)
            || candidate.Length > NhAiMcpResultMetadata.MaxMessageFormatLength
            || values.Length > NhAiMcpResultMetadata.MaxMessageArguments
            || !TryReadSpecifiers(candidate, values.Length, out var specifiers))
        {
            return false;
        }

        var rendered = new object?[values.Length];
        var published = new JsonArray();
        for (var index = 0; index < values.Length; index++)
        {
            if (!TryRender(values[index], specifiers[index], out var shown)
                || shown.Length > NhAiMcpResultMetadata.MaxMessageArgumentLength)
            {
                return false;
            }

            rendered[index] = shown;
            published.Add(ToJson(values[index], specifiers[index], shown));
        }

        // The arguments say no more than the text: the format filled with what each argument
        // rendered gives the text exactly, or nothing is published.
        string filled;
        try
        {
            filled = string.Format(CultureInfo.InvariantCulture, candidate, rendered);
        }
        catch (FormatException)
        {
            return false;
        }
        if (!string.Equals(filled, text, StringComparison.Ordinal))
        {
            return false;
        }

        format = candidate;
        arguments = published;
        return true;
    }

    /// <summary>
    /// The message of a fixed text: the text as its own format, its braces escaped, without
    /// arguments.
    /// </summary>
    public static FormattableString Fixed(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var format = text
            .Replace("{", "{{", StringComparison.Ordinal)
            .Replace("}", "}}", StringComparison.Ordinal);
        return FormattableStringFactory.Create(format);
    }

    /// <summary>
    /// Reads the specifier every argument is rendered with, empty for none. Fails for an argument
    /// the format does not render, an item without an argument, an argument rendered with two
    /// different specifiers and an item other than <c>{index[,alignment][:specifier]}</c>.
    /// </summary>
    private static bool TryReadSpecifiers(
        string format,
        int count,
        [NotNullWhen(true)] out string[]? specifiers)
    {
        specifiers = null;
        var found = new string?[count];
        var position = 0;
        while (position < format.Length)
        {
            var character = format[position];
            if (character is not ('{' or '}'))
            {
                position++;
                continue;
            }

            // An escaped brace is text.
            if (position + 1 < format.Length && format[position + 1] == character)
            {
                position += 2;
                continue;
            }
            if (character == '}')
            {
                return false;
            }

            var end = format.IndexOf('}', position + 1);
            if (end < 0
                || !TryReadItem(format.AsSpan(position + 1, end - position - 1), out var index, out var specifier)
                || index >= count
                || (found[index] is { } earlier && !string.Equals(earlier, specifier, StringComparison.Ordinal)))
            {
                return false;
            }

            found[index] = specifier;
            position = end + 1;
        }

        // An argument the text does not show is never published.
        if (found.Any(specifier => specifier is null))
        {
            return false;
        }

        specifiers = Array.ConvertAll(found, specifier => specifier!);
        return true;
    }

    private static bool TryReadItem(ReadOnlySpan<char> item, out int index, out string specifier)
    {
        index = -1;
        specifier = string.Empty;

        var colon = item.IndexOf(':');
        var head = colon < 0 ? item : item[..colon];
        if (colon >= 0)
        {
            var tail = item[(colon + 1)..];
            if (tail.Contains('{'))
            {
                return false;
            }
            specifier = tail.ToString();
        }

        var comma = head.IndexOf(',');
        var position = comma < 0 ? head : head[..comma];
        if (comma >= 0
            && !int.TryParse(
                head[(comma + 1)..].Trim(),
                NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture,
                out _))
        {
            return false;
        }

        return int.TryParse(position, NumberStyles.None, CultureInfo.InvariantCulture, out index);
    }

    /// <summary>
    /// Renders an argument the way a composite format renders it with the invariant culture.
    /// </summary>
    private static bool TryRender(object? value, string specifier, [NotNullWhen(true)] out string? shown)
    {
        try
        {
            shown = value switch
            {
                null => string.Empty,
                IFormattable formattable => formattable.ToString(
                    specifier.Length == 0 ? null : specifier,
                    CultureInfo.InvariantCulture),
                _ => value.ToString() ?? string.Empty
            };
            return true;
        }
        catch (Exception)
        {
            // An argument that cannot render leaves its message without a format, as a safe
            // formattable string leaves it in the text; the text itself stays as it was.
            shown = null;
            return false;
        }
    }

    private static JsonNode? ToJson(object? value, string specifier, string shown)
    {
        if (specifier.Length > 0)
        {
            // The specifier decides what the text shows, such as two decimals or only the date.
            return JsonValue.Create(shown);
        }

        return value switch
        {
            null => null,
            string text => JsonValue.Create(text),
            bool flag => JsonValue.Create(flag),
            sbyte number => JsonValue.Create(number),
            byte number => JsonValue.Create(number),
            short number => JsonValue.Create(number),
            ushort number => JsonValue.Create(number),
            int number => JsonValue.Create(number),
            uint number => JsonValue.Create(number),
            long number => JsonValue.Create(number),
            ulong number => JsonValue.Create(number),
            float number when float.IsFinite(number) => JsonValue.Create(number),
            double number when double.IsFinite(number) => JsonValue.Create(number),
            decimal number => JsonValue.Create(number),
            // The invariant general format shows dates and times to the second.
            DateTime moment => JsonValue.Create(
                moment.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture)),
            DateTimeOffset moment => JsonValue.Create(
                moment.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture)),
            DateOnly day => JsonValue.Create(day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
            _ => JsonValue.Create(shown)
        };
    }
}
