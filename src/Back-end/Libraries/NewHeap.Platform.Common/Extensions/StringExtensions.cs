using System.Dynamic;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace NewHeap.Platform.Common;

public static partial class StringExtensions
{
    public static string SafeMaxStringLength(this string input, int maxLength)
    {
        input = string.IsNullOrWhiteSpace(input) || input.Length <= maxLength
            ? input
            : input.Substring(0, maxLength);

        return input;
    }

    public static bool ToBoolean(this string str)
    {
        if (bool.TryParse(str, out var booleanValue))
        {
            return booleanValue;
        }

        if (int.TryParse(str, out var integerValue))
        {
            return integerValue != 0;
        }

        return false;
    }

    /// <summary>
    ///     Attempts to format string as JSON, if it fails the string will be returned
    /// </summary>
    /// <param name="input"></param>
    /// <returns></returns>
    public static string FormatJson(this string input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return input;
        }

        try
        {
            var doc = JsonSerializer.Deserialize<ExpandoObject>(input);
            return JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (JsonException)
        {
            return input;
        }
    }

    public static string StripHTML(this string input)
    {
        return Regex.Replace(input, "<.*?>", string.Empty);
    }
}
