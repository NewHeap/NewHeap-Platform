using NewHeap.Media.Modules;
using NhMedia.Http;
using Microsoft.AspNetCore.Http;

// ReSharper disable once CheckNamespace
namespace NhMedia;

public static class HttpContextExtensions
{
    public static bool IsNhMediaEndpoint(this HttpContext context)
    {
        return GetValueOrDefault<bool>(context, NhMediaHttpConstants.IS_NH_MEDIA_ENDPOINT_HTTPCONTEXT_KEY, false);
    }

    public static ActionType? GetNhMediaActionType(this HttpContext context)
    {
        if (!IsNhMediaEndpoint(context))
        {
            return null;
        }
        
        return GetValueOrDefault<ActionType?>(context, NhMediaHttpConstants.HTTPCONTEXT_ACTION_KEY, null);
    }

    private static T? GetValueOrDefault<T>(HttpContext context, string key, T? defaultValue)
    {
        if (!context.Items.TryGetValue(key, out var result))
        {
            return defaultValue;
        }

        return (T?)(result ?? defaultValue);
    }
}