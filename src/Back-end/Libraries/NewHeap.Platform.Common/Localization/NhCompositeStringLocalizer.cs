using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using System.Text.RegularExpressions;

namespace NewHeap.Platform.Common.Localization;

public class NhCompositeStringLocalizer : IStringLocalizer
{
    private readonly IReadOnlyList<IStringLocalizer> _localizers;
    private readonly ILogger<NhCompositeStringLocalizer> _logger;

    public NhCompositeStringLocalizer(
        IEnumerable<IStringLocalizer> localizers,
        ILogger<NhCompositeStringLocalizer> logger)
    {
        _localizers = localizers.ToList();
        _logger = logger;
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
                catch (Exception exception)
                {
                    _logger.LogDebug(
                        exception,
                        "Localization provider {LocalizationProvider} failed to resolve resource {ResourceName}",
                        loc.GetType().Name,
                        name);
                }
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
                catch (Exception exception)
                {
                    _logger.LogDebug(
                        exception,
                        "Localization provider {LocalizationProvider} failed to format resource {ResourceName}",
                        loc.GetType().Name,
                        name);
                }
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
