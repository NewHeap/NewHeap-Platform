using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace NewHeap.Platform.Common.Utilities;

public class SafeFormattableString : FormattableString
{
    private static string SafeStringify(object? value)
    {
        try
        {
            return value?.ToString() ?? "";
        }
        catch (Exception ex)
        {
            return $"[[ToString() failed: {ex.GetType().Name}]]";
        }
    }

    private readonly string _format;
    private readonly object?[] _arguments;

    public SafeFormattableString(string format, object?[] arguments)
    {
        _format = format ?? "";
        _arguments = arguments ?? [];
    }

    public override string Format => _format;

    public override object?[] GetArguments() => _arguments;

    public override int ArgumentCount => _arguments.Length;

    private string SafeFormat()
    {
        var safeArgs = _arguments.Select(SafeStringify).ToArray();

        try
        {
            return string.Format(_format, safeArgs);
        }
        catch
        {
            return _format;
        }
    }

    public override string ToString(IFormatProvider? formatProvider)
    {
        return SafeFormat();
    }

    public override string ToString()
    {
        return SafeFormat();
    }

    public override object? GetArgument(int index)
    {
        return _arguments.ElementAt(index);
    }
}

public static class SafeFormattableStringFactory
{
    public static FormattableString Create(string? format, params object?[] args)
    {
        return new SafeFormattableString(format ?? "", args ?? []);
    }
}