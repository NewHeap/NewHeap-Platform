using System.Globalization;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NewHeap.Platform.AI.Chat.Runtime;

namespace NewHeap.Platform.AI.Chat.AspNet;

/// <summary>
/// Presents approvals in every UI culture the application's request localization supports, so a
/// proposal a headless run made reads in each approver's own language. NewHeap's ASP.NET setup fills
/// the supported cultures from <c>NewHeap:PlatformAspNetCommon:Settings:SupportedCultures</c>; without
/// them approvals are presented in the default request culture and the culture of the turn. At most
/// <see cref="NhAssistantPresentationLimits.MaxCultures"/> are presented, and the first call that drops
/// more logs one warning.
/// </summary>
internal sealed class NhAssistantRequestPresentationCultures(
    IOptions<RequestLocalizationOptions> localization,
    ILogger<NhAssistantRequestPresentationCultures>? logger = null)
    : INhAssistantPresentationCultures
{
    private int _dropWarned;

    public IReadOnlyList<CultureInfo> GetCultures()
    {
        var options = localization.Value;
        IEnumerable<CultureInfo> cultures = options.SupportedUICultures is { Count: > 0 } supported
            ? supported
            : [options.DefaultRequestCulture.UICulture];
        var distinct = cultures
            .DistinctBy(culture => culture.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (distinct.Length > NhAssistantToolPresentationResolver.MaxLocalizedCultures
            && Interlocked.Exchange(ref _dropWarned, 1) == 0)
        {
            logger?.LogWarning(
                "The application supports {CultureCount} UI cultures; approvals are presented in the first {MaxCultures} only.",
                distinct.Length,
                NhAssistantToolPresentationResolver.MaxLocalizedCultures);
        }

        return distinct
            .Take(NhAssistantToolPresentationResolver.MaxLocalizedCultures)
            .ToArray();
    }
}
