using NewHeap.Media.Modules;

namespace NewHeap.Media;

/// <summary>
/// The display strings of a <see cref="FileReference"/> that can be localized: <see cref="FileReference.Title"/>,
/// <see cref="FileReference.Description"/> and <see cref="FileReference.AltText"/>. Names are matched without
/// regard to case, as clients send them in camel case.
/// </summary>
/// <remarks>
/// The file's name, folder, tags, metadata and other properties identify or classify the file for every
/// language and are never replaced by a localization, including one stored before this restriction.
/// </remarks>
internal static class LocalizableFileProperties
{
    private static readonly IReadOnlyDictionary<string, Action<FileReference, string?>> Setters =
        new Dictionary<string, Action<FileReference, string?>>(StringComparer.OrdinalIgnoreCase)
        {
            [nameof(FileReference.Title)] = (file, value) => file.Title = value,
            [nameof(FileReference.Description)] = (file, value) => file.Description = value,
            [nameof(FileReference.AltText)] = (file, value) => file.AltText = value
        };

    internal static bool Contains(string? propertyName)
    {
        return propertyName != null && Setters.ContainsKey(propertyName);
    }

    internal static bool TryGetSetter(string propertyName, out Action<FileReference, string?> setter)
    {
        return Setters.TryGetValue(propertyName, out setter!);
    }
}
