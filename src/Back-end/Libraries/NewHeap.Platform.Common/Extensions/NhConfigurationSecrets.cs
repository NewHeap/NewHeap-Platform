using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;

namespace NewHeap.Platform.Common;

/// <summary>
/// Reads secrets from NewHeap configuration. <c>ConfigureNhCommonConfiguration</c> loads the secrets
/// files under the <see cref="Prefix"/> prefix and resolves <c>${Secrets:…}</c> references in
/// appsettings. A reference whose secret is missing stays as literal text, so a missing secret would
/// otherwise become a value everyone can read in the appsettings file.
/// </summary>
public static partial class NhConfigurationSecrets
{
    /// <summary>The prefix the secrets files are loaded under.</summary>
    public const string Prefix = "Secrets";

    /// <summary>
    /// Returns the secret under <paramref name="key"/>: the key itself (appsettings, environment
    /// variables, command line), otherwise <c>Secrets:{key}</c> from the secrets files. A blank value
    /// or one that still holds an unresolved <c>${…}</c> reference counts as missing.
    /// </summary>
    /// <returns>The secret, or <see langword="null"/> when it is not configured.</returns>
    public static string? Read(IConfiguration configuration, string key)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        return Usable(configuration[key]) ?? Usable(configuration[$"{Prefix}:{key}"]);
    }

    /// <summary>
    /// Whether <paramref name="value"/> still holds a <c>${…}</c> reference to a configuration key,
    /// which the configuration could not resolve.
    /// </summary>
    public static bool IsUnresolvedReference(string? value)
    {
        return value is not null && ReferencePattern().IsMatch(value);
    }

    /// <summary>
    /// The configuration keys whose values still hold an unresolved <c>${…}</c> reference, ordered by
    /// key. The values are never returned: they belong to secrets that are missing.
    /// </summary>
    public static IReadOnlyList<string> FindUnresolvedReferences(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        return configuration.AsEnumerable()
            .Where(entry => IsUnresolvedReference(entry.Value))
            .Select(entry => entry.Key)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string? Usable(string? value)
    {
        return string.IsNullOrWhiteSpace(value) || IsUnresolvedReference(value) ? null : value;
    }

    [GeneratedRegex(@"\$\{[A-Za-z0-9_.:\-]+\}", RegexOptions.CultureInvariant)]
    private static partial Regex ReferencePattern();
}
