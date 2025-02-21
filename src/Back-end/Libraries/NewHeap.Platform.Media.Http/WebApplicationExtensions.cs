using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using NhMedia.Http;
using NewHeap.Media.Modules;

// ReSharper disable once CheckNamespace
namespace NewHeap.Media;

public static class WebApplicationExtensions
{
    public static RouteGroupBuilder MapNhMediaEndpoints(this IEndpointRouteBuilder app, string prefix = "media")
    {
        var group = app.MapGroup(prefix);
        group.MapGet("list", List).WithDescription("List files and folders");
        group.MapGet("download", Download).WithDescription("Download a file");
        group.MapGet("search", Search).WithDescription("Search files");

        group.MapPost("upload", Upload).DisableAntiforgery().WithDescription("Upload a file");
        group.MapPost("folder", CreateFolder).WithDescription("Create a folder");
        group.MapPost("file/localize", LocalizeFile).WithDescription("Add localization to a file");

        group.MapDelete("folder", DeleteFolder).WithDescription("Delete a folder and all files and subfolders");
        group.MapDelete("file", DeleteFile).WithDescription("Delete a file");
        
        return group;
    }

    private static async Task<Ok<IEnumerable<FileReference>>> Search(string? path, string searchTerm, string? language,
        IMediaLibraryService mediaLibraryService)
    {
        var result = await mediaLibraryService.Search(path, searchTerm, language);
        return TypedResults.Ok(result);
    }

    private static async Task<NoContent> DeleteFile(string? path, string fileName,
        IMediaLibraryService mediaLibraryService)
    {
        await mediaLibraryService.DeleteFile(path, fileName);
        return TypedResults.NoContent();
    }

    private static async Task<NoContent> DeleteFolder(string? path, string folderName,
        IMediaLibraryService mediaLibraryService)
    {
        await mediaLibraryService.DeleteFolder(path, folderName);
        return TypedResults.NoContent();
    }

    private static async Task<Ok<FolderReference>> CreateFolder(string? path, string folderName,
        IMediaLibraryService mediaLibraryService)
    {
        var folderRef = await mediaLibraryService.CreateFolder(path, folderName);
        return TypedResults.Ok(folderRef);
    }

    private static async Task<Results<FileStreamHttpResult, NotFound>> Download(string? path, string fileName,
        IMediaLibraryService mediaLibraryService)
    {
        var stream = await mediaLibraryService.DownloadFile(path, fileName);
        if (stream == null)
        {
            return TypedResults.NotFound();
        }

        return TypedResults.File(stream, fileName);
    }

    private static async Task<Ok<FileReference>> Upload([FromQuery] string? path, [FromForm] UploadRequest request,
        IMediaLibraryService mediaLibraryService)
    {
        var fileRef = await mediaLibraryService.GetFile(path, request.FileName);
        if (fileRef == null)
        {
            fileRef = await mediaLibraryService.CreateFile(path, request.FileName, request.File.OpenReadStream());
        }
        else
        {
            await mediaLibraryService.UpdateFile(path, request.FileName, request.File.OpenReadStream());
        }

        return TypedResults.Ok(fileRef);
    }

    private static async Task<Results<NoContent, NotFound>> LocalizeFile(
        [FromQuery, Required] string? path,
        [FromQuery, Required] string fileName,
        [FromQuery, Required] string language,
        [FromQuery, Required] string propertyName,
        [FromQuery] string value,
        IMediaLibraryService mediaLibraryService)
    {
        var reference = await mediaLibraryService.GetFile(path, fileName, null);
        if (reference == null)
        {
            return TypedResults.NotFound();
        }

        await mediaLibraryService.LocalizeField(reference.Id, propertyName, language, value);
        return TypedResults.NoContent();
    }

    private static async Task<Ok<FolderContents>> List(string? path, string? language,
        IMediaLibraryService mediaLibraryService)
    {
        var contents = await mediaLibraryService.GetFolder(path, language);
        return TypedResults.Ok(contents);
    }
}