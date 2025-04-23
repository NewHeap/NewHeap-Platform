using Microsoft.AspNetCore.Builder;

namespace NhMedia.Http;

public class MediaLibraryRouteBuilder
{
    public Action<RouteHandlerBuilder>? List { get; set; }
    public Action<RouteHandlerBuilder>? Download { get; set; }
    
    public Action<RouteHandlerBuilder>? Search { get; set; }
    
    public Action<RouteHandlerBuilder>? UploadFile { get; set; }
    
    public Action<RouteHandlerBuilder>? CreateFolder { get; set; }
    
    public Action<RouteHandlerBuilder>? LocalizeFile { get; set; }
    
    public Action<RouteHandlerBuilder>? UpdateTags { get; set; }
    
    public Action<RouteHandlerBuilder>? UpdateFile { get; set; }
    
    public Action<RouteHandlerBuilder>? UpdateFolder { get; set; }

    public Action<RouteHandlerBuilder>? DeleteFolder { get; set; }
    
    public Action<RouteHandlerBuilder>? DeleteFile { get; set; }
    
    public Action<RouteHandlerBuilder>? AllRoutes { get; set; }
}