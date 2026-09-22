using Microsoft.Extensions.Localization;

namespace NewHeap.Platform.AI.AspNet.Mvc;

public sealed record NhAiBridgeResourcePresentation(
    string Resource,
    string TitleKey,
    string SummaryKey,
    string FallbackTitle,
    string FallbackSummary);

/// <summary>Localized gateway presentation configured by a consumer-owned resource type.</summary>
public sealed class NhAiBridgeResourcePresentationOptions
{
    private readonly Dictionary<string, NhAiBridgeResourcePresentation> _resources =
        new(StringComparer.Ordinal);

    internal Type ResourceSourceType { get; set; } = null!;

    public IReadOnlyDictionary<string, NhAiBridgeResourcePresentation> Resources => _resources;

    /// <summary>When true, startup fails if the current-culture localizer cannot resolve a key.</summary>
    public bool RequireLocalizedValues { get; set; } = true;

    public NhAiBridgeResourcePresentationOptions Add(
        string resource,
        string titleKey,
        string summaryKey,
        string fallbackTitle,
        string fallbackSummary)
    {
        NhAiMvcBridgeNames.ValidateSegment(resource, nameof(resource));
        ArgumentException.ThrowIfNullOrWhiteSpace(titleKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(summaryKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(fallbackTitle);
        ArgumentException.ThrowIfNullOrWhiteSpace(fallbackSummary);
        if (!_resources.TryAdd(resource, new NhAiBridgeResourcePresentation(
            resource,
            titleKey,
            summaryKey,
            fallbackTitle,
            fallbackSummary)))
        {
            throw new InvalidOperationException(
                $"AI bridge resource presentation '{resource}' is already configured.");
        }
        return this;
    }
}

internal sealed class NhAiLocalizedBridgeResourceDescriber(
    IStringLocalizerFactory localizerFactory,
    NhAiBridgeResourcePresentationOptions options) : INhAiBridgeResourceDescriber
{
    private readonly IStringLocalizer _localizer = localizerFactory.Create(options.ResourceSourceType);

    public NhAiBridgeResourceDescription Describe(NhAiBridgeResourceDescription description)
    {
        ArgumentNullException.ThrowIfNull(description);
        if (!options.Resources.TryGetValue(description.Resource, out var presentation))
        {
            return description;
        }
        return description with
        {
            Title = Resolve(presentation.TitleKey, presentation.FallbackTitle),
            Description = Resolve(presentation.SummaryKey, presentation.FallbackSummary)
        };
    }

    private string Resolve(string key, string fallback)
    {
        var localized = _localizer[key];
        return localized.ResourceNotFound ? fallback : localized.Value;
    }
}

internal static class NhAiBridgeResourcePresentationValidator
{
    public static void Validate(
        NhAiBridgeResourcePresentationOptions options,
        NhAiMvcBridgeToolCatalog catalog,
        IStringLocalizerFactory localizerFactory)
    {
        var resources = catalog.GatewayResources.ToHashSet(StringComparer.Ordinal);
        var orphaned = options.Resources.Keys.Except(resources, StringComparer.Ordinal).OrderBy(value => value).ToArray();
        if (orphaned.Length > 0)
        {
            throw new InvalidOperationException(
                $"AI bridge localized resource presentation contains orphaned resources: [{string.Join(", ", orphaned)}].");
        }
        if (!options.RequireLocalizedValues)
        {
            return;
        }

        var localizer = localizerFactory.Create(options.ResourceSourceType);
        var missingKeys = options.Resources.Values
            .SelectMany(item => new[] { item.TitleKey, item.SummaryKey })
            .Distinct(StringComparer.Ordinal)
            .Where(key => localizer[key].ResourceNotFound)
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToArray();
        if (missingKeys.Length > 0)
        {
            throw new InvalidOperationException(
                $"AI bridge localized resource keys are missing: [{string.Join(", ", missingKeys)}].");
        }
    }
}
