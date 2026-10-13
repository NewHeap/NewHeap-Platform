using System.Globalization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Localization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace NewHeap.Platform.AI.Chat.AspNet;

/// <summary>
/// Lets an assistant request that names only a language, such as <c>Accept-Language: en</c> from
/// the Angular client, use the application's supported culture of that language instead of the
/// default culture. Request localization only matches exact cultures and their parents, so
/// <c>en</c> would otherwise fall back to a default such as <c>nl-NL</c>, and the caller would
/// read answers and agent proposals in the wrong language.
/// </summary>
internal static class NhAssistantRequestCulture
{
    public static async ValueTask<object?> MatchLanguageAsync(
        EndpointFilterInvocationContext context,
        EndpointFilterDelegate next)
    {
        var httpContext = context.HttpContext;
        // A provider that matched (query string, cookie or an exact Accept-Language) wins.
        if (httpContext.Features.Get<IRequestCultureFeature>()?.Provider is null
            && FindSupportedCulture(httpContext) is { } culture)
        {
            CultureInfo.CurrentCulture = culture;
            CultureInfo.CurrentUICulture = culture;
        }

        return await next(context);
    }

    private static CultureInfo? FindSupportedCulture(HttpContext httpContext)
    {
        var languages = httpContext.Request.GetTypedHeaders().AcceptLanguage;
        if (languages.Count == 0)
        {
            return null;
        }

        var supported = httpContext.RequestServices
            .GetRequiredService<IOptions<RequestLocalizationOptions>>()
            .Value
            .SupportedUICultures ?? [];
        foreach (var language in languages.OrderByDescending(item => item.Quality ?? 1))
        {
            var name = language.Value.Value;
            if (string.IsNullOrWhiteSpace(name) || name == "*")
            {
                continue;
            }

            var twoLetter = name.Split('-')[0];
            var match = supported.FirstOrDefault(culture =>
                string.Equals(culture.TwoLetterISOLanguageName, twoLetter, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                return match;
            }
        }

        return null;
    }
}
