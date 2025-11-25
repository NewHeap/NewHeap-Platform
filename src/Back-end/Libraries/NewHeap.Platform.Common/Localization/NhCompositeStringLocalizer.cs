using Microsoft.Extensions.Localization;

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

            string value;
            try
            {
                value = arguments != null && arguments.Length > 0
                    ? string.Format(name, arguments)
                    : name;
            }
            catch (FormatException)
            {
                value = name;
            }

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
}
