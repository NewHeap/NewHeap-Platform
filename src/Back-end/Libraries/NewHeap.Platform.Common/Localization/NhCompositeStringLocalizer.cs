using Microsoft.Extensions.Localization;
using System.Text.RegularExpressions;

namespace NewHeap.Platform.Common.Localization;

public class NhCompositeStringLocalizer : IStringLocalizer
{
    private readonly IReadOnlyList<IStringLocalizer> _localizers;

    public NhCompositeStringLocalizer(IEnumerable<IStringLocalizer> localizers)
    {
        _localizers = localizers.ToList();
    }

    public LocalizedString this[string name]
    {
        get
        {
            foreach (var loc in _localizers)
            {
                try
                {
                    var str = loc[name];
                    if (!str.ResourceNotFound)
                        return str;
                }
                catch { }
            }
            return new LocalizedString(name, name, true);
        }
    }

    public LocalizedString this[string name, params object[] arguments]
    {
        get
        {
            foreach (var loc in _localizers)
            {
                try
                {
                    var str = loc[name, arguments];
                    if (!str.ResourceNotFound)
                        return str;
                }
                catch { }
            }

            var value = SafeFormat(name, arguments);

            return new LocalizedString(name, value, resourceNotFound: true);
        }
    }

    public IEnumerable<LocalizedString> GetAllStrings(bool includeAncestorCultures)
    {
        var all = new Dictionary<string, LocalizedString>(StringComparer.OrdinalIgnoreCase);
        foreach (var loc in _localizers)
        {
            foreach (var s in loc.GetAllStrings(includeAncestorCultures))
                all[s.Name] = s;
        }
        return all.Values;
    }

    private static string SafeFormat(string format, object[] arguments)
    {
        if (arguments == null || arguments.Length == 0)
            return format;

        try
        {
            return string.Format(format, arguments);
        }
        catch (FormatException)
        {
            var result = format;

            var matches = Regex.Matches(format, "{([a-zA-Z0-9_]+)}");

            if (matches.Count > 0)
            {
                int index = 0;
                foreach (Match match in matches)
                {
                    var replaceValue = (index >= arguments.Length)
                        ? null
                        : arguments[index]?.ToString();

                    result = result.Replace(match.Value, replaceValue);
                    index++;
                }

                return result;
            }

            return format;
        }
    }
}
