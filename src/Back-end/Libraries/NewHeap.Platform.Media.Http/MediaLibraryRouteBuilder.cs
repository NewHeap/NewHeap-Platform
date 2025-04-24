using Microsoft.AspNetCore.Builder;

namespace NhMedia.Http;

public class MediaLibraryRouteBuilder
{
    internal Action<RouteHandlerBuilder>? ListAction;

    public MediaLibraryRouteBuilder ConfigureList(Action<RouteHandlerBuilder>? list = null)
    {
        ListAction = list;
        return this;
    }

    internal Action<RouteHandlerBuilder>? DownloadAction;

    public MediaLibraryRouteBuilder ConfigureDownload(Action<RouteHandlerBuilder>? download = null)
    {
        DownloadAction = download;
        return this;
    }

    internal Action<RouteHandlerBuilder>? SearchAction { get; set; }

    public MediaLibraryRouteBuilder ConfigureSearch(Action<RouteHandlerBuilder>? search = null)
    {
        SearchAction = search;
        return this;
    }


    internal Action<RouteHandlerBuilder>? UploadFileAction;

    public MediaLibraryRouteBuilder ConfigureUploadFile(Action<RouteHandlerBuilder>? uploadFile = null)
    {
        UploadFileAction = uploadFile;
        return this;
    }

    internal Action<RouteHandlerBuilder>? CreateFolderAction;

    public MediaLibraryRouteBuilder ConfigureCreateFolder(Action<RouteHandlerBuilder>? createFolder = null)
    {
        CreateFolderAction = createFolder;
        return this;
    }

    internal Action<RouteHandlerBuilder>? LocalizeFileAction;

    public MediaLibraryRouteBuilder ConfigureLocalizeFile(Action<RouteHandlerBuilder>? localizeFile = null)
    {
        LocalizeFileAction = localizeFile;
        return this;
    }

    internal Action<RouteHandlerBuilder>? UpdateTagsAction;

    public MediaLibraryRouteBuilder ConfigureUpdateTags(Action<RouteHandlerBuilder>? updateTags = null)
    {
        UpdateTagsAction = updateTags;
        return this;
    }

    internal Action<RouteHandlerBuilder>? UpdateFileAction;

    public MediaLibraryRouteBuilder ConfigureUpdateFile(Action<RouteHandlerBuilder>? updateFile = null)
    {
        UpdateFileAction = updateFile;
        return this;
    }

    internal Action<RouteHandlerBuilder>? UpdateFolderAction;

    public MediaLibraryRouteBuilder ConfigureUpdateFolder(Action<RouteHandlerBuilder>? updateFolder = null)
    {
        UpdateFolderAction = updateFolder;
        return this;
    }

    internal Action<RouteHandlerBuilder>? DeleteFolderAction;

    public MediaLibraryRouteBuilder ConfigureDeleteFolder(Action<RouteHandlerBuilder>? deleteFolder = null)
    {
        DeleteFolderAction = deleteFolder;
        return this;
    }

    internal Action<RouteHandlerBuilder>? DeleteFileAction;

    public MediaLibraryRouteBuilder ConfigureDeleteFile(Action<RouteHandlerBuilder>? deleteFile = null)
    {
        DeleteFileAction = deleteFile;
        return this;
    }

    internal Func<RouteHandlerBuilder, RouteHandlerBuilder>? AllRoutesAction;

    public MediaLibraryRouteBuilder ConfigureAllRoutes(Func<RouteHandlerBuilder, RouteHandlerBuilder>? allRoutes = null)
    {
        AllRoutesAction = allRoutes;
        return this;
    }
}