using System.Text.RegularExpressions;
using NewHeap.Platform.Common.Models;

namespace NewHeap.Platform.AI.Chat;

/// <summary>
/// Resolves secrets of MCP servers that an administrator references as <c>&lt;prefix&gt;:&lt;name&gt;</c>
/// instead of storing them, for example from a vault. Register it for the application's own prefix
/// with <see cref="NhAssistantBuilder.AddMcpSecretResolver{TResolver}"/>. It is a singleton and is
/// called for every request to the server, so a rotated secret applies at once; caching is the
/// resolver's choice. The value is never logged, audited or returned.
/// </summary>
public interface INhAssistantMcpSecretResolver
{
    /// <summary>
    /// Returns the secret named <paramref name="name"/>, or a failed result when there is none.
    /// </summary>
    ValueTask<TaskResult<string>> ResolveAsync(string name, CancellationToken cancellationToken);
}

/// <summary>
/// The syntax of MCP secret references. A reference is <c>&lt;prefix&gt;:&lt;name&gt;</c>: the prefix
/// <see cref="ConfigurationPrefix"/> reads <see cref="ConfigurationSection"/><c>:&lt;name&gt;</c>, any
/// other prefix needs a registered <see cref="INhAssistantMcpSecretResolver"/>. A name has at most 200
/// letters, digits, <c>.</c>, <c>_</c>, <c>/</c> and <c>-</c>, starts with a letter or digit and contains
/// no <c>:</c> or <c>..</c>, so a reference can never reach configuration outside the dedicated section
/// or climb out of a resolver's path.
/// </summary>
public static partial class NhAssistantMcpSecretReferences
{
    public const string ConfigurationPrefix = "config";

    public const string ConfigurationSection = "NewHeap:AI:Assistant:Mcp:Secrets";

    public const int MaxLength = 256;

    /// <summary>
    /// Splits a reference into its prefix and name; <see langword="false"/> when the syntax is invalid.
    /// </summary>
    public static bool TryParse(string? reference, out string prefix, out string name)
    {
        prefix = string.Empty;
        name = string.Empty;
        if (string.IsNullOrEmpty(reference) || reference.Length > MaxLength)
        {
            return false;
        }
        var separator = reference.IndexOf(':');
        if (separator <= 0)
        {
            return false;
        }
        var candidatePrefix = reference[..separator];
        var candidateName = reference[(separator + 1)..];
        if (!IsValidPrefix(candidatePrefix)
            || !NamePattern().IsMatch(candidateName)
            || candidateName.Contains("..", StringComparison.Ordinal))
        {
            return false;
        }
        prefix = candidatePrefix;
        name = candidateName;
        return true;
    }

    /// <summary>
    /// Whether a prefix is dash-case, starts with a letter and has at most 32 characters.
    /// </summary>
    public static bool IsValidPrefix(string? prefix)
    {
        return prefix is not null && PrefixPattern().IsMatch(prefix);
    }

    [GeneratedRegex("^[a-z][a-z0-9-]{0,31}$", RegexOptions.CultureInvariant)]
    private static partial Regex PrefixPattern();

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._/-]{0,199}$", RegexOptions.CultureInvariant)]
    private static partial Regex NamePattern();
}

internal sealed record NhAssistantMcpSecretResolverRegistration(string Prefix, Type ResolverType);
