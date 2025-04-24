using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using NewHeap.Media;
using NewHeap.Media.Modules;

namespace NhMedia.Http;

public class MediaContextEndpointFilter : IEndpointFilter
{
    private readonly NhMediaContext _mediaContext;

    public MediaContextEndpointFilter(NhMediaContext mediaContext)
    {
        _mediaContext = mediaContext;
    }
    
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var prefix = _mediaContext.Get<string>(NhMediaHttpConstants.API_PREFIX_CONTEXT_KEY);

        var route = (context.HttpContext.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText?.ToLower();
        if (!string.IsNullOrWhiteSpace(route) && route.StartsWith(prefix))
        {
            route = route[prefix.Length..];
            var actionType = GetActionTypeByRoute(route, HttpMethod.Parse(context.HttpContext.Request.Method));
            context.HttpContext.Items[NhMediaHttpConstants.HTTPCONTEXT_ACTION_KEY] = actionType;
            context.HttpContext.Items[NhMediaHttpConstants.IS_NH_MEDIA_ENDPOINT_HTTPCONTEXT_KEY] = true;
        }
        
        return await next(context);
    }

    private ActionType? GetActionTypeByRoute(string route, HttpMethod method)
    {
        if (method == HttpMethod.Get)
        {
            if (
                route.StartsWith("list")
                || route.StartsWith("download")
                || route.StartsWith("search")
            )
            {
                return ActionType.Read;
            }    
        }
        else if (method == HttpMethod.Post)
        {
            if (
                route.StartsWith("file/localize")
                || route.StartsWith("file/tags")
                )
            {
                return ActionType.Update;
            }

            return ActionType.Create;
        }
        else if (method == HttpMethod.Put)
        {
            return ActionType.Update;
        }
        else if (method == HttpMethod.Delete)
        {
            return ActionType.Delete;
        }
        
        return null;
    }
}
