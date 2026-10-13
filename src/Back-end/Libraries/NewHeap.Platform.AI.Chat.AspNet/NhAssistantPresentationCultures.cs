using System.Globalization;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Options;
using NewHeap.Platform.AI.Chat.Runtime;

namespace NewHeap.Platform.AI.Chat.AspNet;

/// <summary>
/// Presents approvals in every UI culture the application's request localization supports, so a
/// proposal a headless run made reads in each approver's own language.
/// </summary>
internal sealed class NhAssistantRequestPresentationCultures(IOptions<RequestLocalizationOptions> localization)
    : INhAssistantPresentationCultures
{
    public IReadOnlyList<CultureInfo> GetCultures()
    {
        var options = localization.Value;
        IEnumerable<CultureInfo> cultures = options.SupportedUICultures is { Count: > 0 } supported
            ? supported
            : [options.DefaultRequestCulture.UICulture];
        return cultures
            .DistinctBy(culture => culture.Name, StringComparer.OrdinalIgnoreCase)
            .Take(NhAssistantToolPresentationResolver.MaxLocalizedCultures)
            .ToArray();
    }
}
