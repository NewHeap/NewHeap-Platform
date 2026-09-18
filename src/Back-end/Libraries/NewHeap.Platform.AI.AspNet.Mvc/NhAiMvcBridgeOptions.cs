using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace NewHeap.Platform.AI.AspNet.Mvc;

/// <summary>
/// Execution bounds applied to every bridged tool unless <see cref="NhAiBridgeToolAttribute"/>
/// narrows them.
/// </summary>
public sealed class NhAiMvcBridgeToolDefaults
{
    public int MaxResultBytes { get; set; } = 65_536;

    public int TimeoutSeconds { get; set; } = 30;

    public int MaxInputBytes { get; set; } = 16_384;

    /// <summary>The number of concurrent calls per tool in this process.</summary>
    public int MaxConcurrency { get; set; } = 16;
}

/// <summary>
/// The configuration of the API bridge, built by <see cref="NhAiMvcBridgeBuilder"/>.
/// </summary>
public sealed class NhAiMvcBridgeOptions
{
    internal List<string> IncludedControllers { get; } = [];

    internal List<string> ExcludedControllers { get; } = [];

    internal List<string> ExcludedActions { get; } = [];

    public string? ToolSetId { get; internal set; }

    public string? SelfBaseUrl { get; internal set; }

    internal Func<IServiceProvider, string?>? SelfBaseUrlResolver { get; set; }

    public IReadOnlyList<string> IncludeControllerPatterns => IncludedControllers;

    public IReadOnlyList<string> ExcludeControllerPatterns => ExcludedControllers;

    public IReadOnlyList<string> ExcludeActionPatterns => ExcludedActions;

    public bool RequireExplicitPolicy { get; internal set; } = true;

    public bool IncludeDeleteActions { get; internal set; }

    public bool IncludeFileUploads { get; internal set; }

    public bool McpExposureEnabled { get; internal set; }

    public int ContractVersion { get; internal set; } = 1;

    public Type ConventionsType { get; internal set; } = typeof(NhAiMvcBridgeDefaultConventions);

    public Type? InnerDiscoveryPolicyType { get; internal set; }

    public NhAiMvcBridgeToolDefaults ToolDefaults { get; } = new();
}

/// <summary>
/// Configures <see cref="NhAiMvcBridgeServiceCollectionExtensions.AddNewHeapPlatformAIMvcBridge"/>.
/// </summary>
public sealed class NhAiMvcBridgeBuilder
{
    private readonly IServiceCollection _services;
    private readonly NhAiMvcBridgeOptions _options;

    internal NhAiMvcBridgeBuilder(IServiceCollection services, NhAiMvcBridgeOptions options)
    {
        _services = services;
        _options = options;
    }

    /// <summary>The dash-case tool set id that prefixes every tool id, such as <c>sample-api</c>. Required.</summary>
    public NhAiMvcBridgeBuilder UseToolSetId(string toolSetId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolSetId);
        NhAiMvcBridgeNames.ValidateSegment(toolSetId, nameof(toolSetId));
        _options.ToolSetId = toolSetId;
        return this;
    }

    /// <summary>
    /// The absolute base URL at which the application reaches its own API. Without a value the
    /// bridge reads <c>NewHeap:AI:Bridge:SelfBaseUrl</c>; a missing URL fails at startup.
    /// </summary>
    public NhAiMvcBridgeBuilder UseSelfBaseUrl(string? selfBaseUrl)
    {
        _options.SelfBaseUrl = selfBaseUrl;
        return this;
    }

    /// <summary>
    /// Resolves the self base URL from the application services when the catalog is built,
    /// for example from a configuration key that is not available during registration.
    /// </summary>
    public NhAiMvcBridgeBuilder UseSelfBaseUrl(Func<IServiceProvider, string?> resolveSelfBaseUrl)
    {
        ArgumentNullException.ThrowIfNull(resolveSelfBaseUrl);
        _options.SelfBaseUrlResolver = resolveSelfBaseUrl;
        return this;
    }

    /// <summary>
    /// Controller names without the <c>Controller</c> suffix; <c>*</c> is a wildcard. At least one
    /// pattern is required; <c>"*"</c> includes every controller.
    /// </summary>
    public NhAiMvcBridgeBuilder IncludeControllers(params string[] patterns)
    {
        AddPatterns(_options.IncludedControllers, patterns, nameof(patterns));
        return this;
    }

    public NhAiMvcBridgeBuilder ExcludeControllers(params string[] patterns)
    {
        AddPatterns(_options.ExcludedControllers, patterns, nameof(patterns));
        return this;
    }

    /// <summary>Action patterns as <c>&lt;Controller&gt;.&lt;Action&gt;</c>; <c>*</c> is a wildcard.</summary>
    public NhAiMvcBridgeBuilder ExcludeActions(params string[] patterns)
    {
        AddPatterns(_options.ExcludedActions, patterns, nameof(patterns));
        return this;
    }

    /// <summary>
    /// When true (default), only actions with a named <c>[Authorize(Policy = ...)]</c> on the
    /// action or controller are published.
    /// </summary>
    public NhAiMvcBridgeBuilder RequireExplicitPolicy(bool required = true)
    {
        _options.RequireExplicitPolicy = required;
        return this;
    }

    /// <summary>
    /// DELETE actions are not supported in v1: destructive tools require a verifier. Passing
    /// <see langword="true"/> fails at startup.
    /// </summary>
    public NhAiMvcBridgeBuilder IncludeDeleteActions(bool include = false)
    {
        _options.IncludeDeleteActions = include;
        return this;
    }

    /// <summary>
    /// When true, actions with <c>IFormFile</c> parameters are published; files travel as
    /// base64 content in the tool input and are bounded by the input-size limit.
    /// </summary>
    public NhAiMvcBridgeBuilder IncludeFileUploads(bool include = false)
    {
        _options.IncludeFileUploads = include;
        return this;
    }

    public NhAiMvcBridgeBuilder UseConventions<TConventions>()
        where TConventions : class, INhAiBridgeConventions
    {
        _options.ConventionsType = typeof(TConventions);
        return this;
    }

    /// <summary>
    /// The discovery policy for every descriptor that is not a bridge descriptor. Without it,
    /// non-bridge tools are not discoverable.
    /// </summary>
    public NhAiMvcBridgeBuilder UseInnerDiscoveryPolicy<TPolicy>()
        where TPolicy : class, INhAiToolDiscoveryPolicy
    {
        _services.TryAddScoped<TPolicy>();
        _options.InnerDiscoveryPolicyType = typeof(TPolicy);
        return this;
    }

    public NhAiMvcBridgeBuilder UseContractVersion(int version)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(version, 1);
        _options.ContractVersion = version;
        return this;
    }

    public NhAiMvcBridgeBuilder WithToolDefaults(Action<NhAiMvcBridgeToolDefaults> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        configure(_options.ToolDefaults);
        return this;
    }

    /// <summary>Publishes bridge tools through MCP in addition to local and agent use.</summary>
    public NhAiMvcBridgeBuilder EnableMcpExposure()
    {
        _options.McpExposureEnabled = true;
        return this;
    }

    private static void AddPatterns(List<string> target, string[] patterns, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(patterns, parameterName);
        foreach (var pattern in patterns)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(pattern, parameterName);
            target.Add(pattern.Trim());
        }
    }
}

/// <summary>
/// Values resolved from options and configuration when the application starts.
/// </summary>
internal sealed record NhAiMvcBridgeRuntimeSettings(string? SelfBaseUrl, bool Enabled)
{
    public const string SelfBaseUrlKey = "NewHeap:AI:Bridge:SelfBaseUrl";
    public const string EnabledKey = "NewHeap:AI:Bridge:Enabled";

    public static NhAiMvcBridgeRuntimeSettings Resolve(NhAiMvcBridgeOptions options, IServiceProvider services)
    {
        var configuration = services.GetService<Microsoft.Extensions.Configuration.IConfiguration>();
        var selfBaseUrl = options.SelfBaseUrl;
        if (string.IsNullOrWhiteSpace(selfBaseUrl) && options.SelfBaseUrlResolver is not null)
        {
            selfBaseUrl = options.SelfBaseUrlResolver(services);
        }
        if (string.IsNullOrWhiteSpace(selfBaseUrl))
        {
            selfBaseUrl = configuration?[SelfBaseUrlKey];
        }

        var enabledValue = configuration?[EnabledKey];
        var enabled = string.IsNullOrWhiteSpace(enabledValue)
            || !bool.TryParse(enabledValue, out var parsed)
            || parsed;
        return new NhAiMvcBridgeRuntimeSettings(selfBaseUrl, enabled);
    }
}

internal static class NhAiMvcBridgeNames
{
    public static bool IsSegment(string? value)
    {
        return !string.IsNullOrWhiteSpace(value)
            && value[0] != '-'
            && value[^1] != '-'
            && !value.Contains("--", StringComparison.Ordinal)
            && value.All(character => character == '-'
                || (character >= 'a' && character <= 'z')
                || (character >= '0' && character <= '9'));
    }

    public static void ValidateSegment(string value, string parameterName)
    {
        if (!IsSegment(value))
        {
            throw new ArgumentException("AI identifiers must use lowercase dash-case.", parameterName);
        }
    }

    public static bool MatchesPattern(string value, string pattern)
    {
        if (string.Equals(pattern, "*", StringComparison.Ordinal))
        {
            return true;
        }

        var regex = "^" + System.Text.RegularExpressions.Regex.Escape(pattern).Replace("\\*", ".*", StringComparison.Ordinal) + "$";
        return System.Text.RegularExpressions.Regex.IsMatch(
            value,
            regex,
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    }

    /// <summary>Converts PascalCase, camelCase or snake_case to lowercase dash-case.</summary>
    public static string ToKebabCase(string value)
    {
        var builder = new System.Text.StringBuilder(value.Length + 8);
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (!char.IsAsciiLetterOrDigit(character))
            {
                AppendSeparator(builder);
                continue;
            }

            if (char.IsAsciiLetterUpper(character) && index > 0)
            {
                var previous = value[index - 1];
                var next = index + 1 < value.Length ? value[index + 1] : '\0';
                var wordStart = char.IsAsciiLetterLower(previous)
                    || char.IsAsciiDigit(previous)
                    || (char.IsAsciiLetterUpper(previous) && char.IsAsciiLetterLower(next));
                if (wordStart)
                {
                    AppendSeparator(builder);
                }
            }
            builder.Append(char.ToLowerInvariant(character));
        }

        while (builder.Length > 0 && builder[^1] == '-')
        {
            builder.Length--;
        }
        return builder.ToString();
    }

    private static void AppendSeparator(System.Text.StringBuilder builder)
    {
        if (builder.Length > 0 && builder[^1] != '-')
        {
            builder.Append('-');
        }
    }
}
