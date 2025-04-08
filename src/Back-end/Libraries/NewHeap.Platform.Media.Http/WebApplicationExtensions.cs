using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using NhMedia.Http;
using NewHeap.Media.Modules;
using NhMedia.Http.Models;

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
        group.MapPost("file/tags", UpdateTags).WithDescription("Update file tags");

        group.MapPut("file/{id:guid}", UpdateFile).WithDescription("Update file (meta)data");
        group.MapPut("folder", UpdateFolder).WithDescription("Update folder properties");

        group.MapDelete("folder", DeleteFolder).WithDescription("Delete a folder and all files and subfolders");
        group.MapDelete("file", DeleteFile).WithDescription("Delete a file");


        return group;
    }

    private static BadRequest<Dictionary<string, string[]>> BadRequest(string error, string field = "")
    {
        return TypedResults.BadRequest(new Dictionary<string, string[]> { { field, [error] } });
    }

    [ApiExplorerSettings(GroupName = "Media")]
    [Tags("Media")]
    [EndpointName("Update folder information")]
    private static async Task<Results<
        Ok<FolderReference>,
        NotFound,
        BadRequest<Dictionary<string, string[]>>
    >> UpdateFolder(
        [FromBody] UpdateFolderRequest request,
        [FromServices] IMediaLibraryService mediaLibrary
    )
    {
        if (string.IsNullOrWhiteSpace(request.NewName) || string.IsNullOrWhiteSpace(request.FolderName))
        {
            return BadRequest("Name is required");
        }

        var result =
            await mediaLibrary.UpdateFolder(request.Path, request.FolderName, request.NewPath, request.NewName);
        if (result != null)
        {
            return TypedResults.Ok(result);
        }

        return TypedResults.NotFound();
    }

    [ApiExplorerSettings(GroupName = "Media")]
    [Tags("Media")]
    [EndpointName("Update file information")]
    private static async Task<Results<Ok<FileReference>, NotFound>> UpdateFile([FromRoute] Guid id,
        [FromBody] FileModel model,
        [FromServices] IMediaLibraryService mediaLibrary)
    {
        var success = await mediaLibrary.UpdateFile(id, model);
        if (success)
        {
            var reference = await mediaLibrary.GetFile(id);
            return TypedResults.Ok(reference);
        }

        return TypedResults.NotFound();
    }

    [ApiExplorerSettings(GroupName = "Media")]
    [Tags("Media")]
    [EndpointName("Update file tags")]
    private static async Task<Results<NoContent, NotFound, BadRequest<Dictionary<string, string[]>>>> UpdateTags(
        [FromBody] UpdateTagsRequest request,
        [FromServices] IMediaLibraryService mediaLibrary
    )
    {
        if (string.IsNullOrWhiteSpace(request?.FileName))
        {
            return BadRequest("Field is required", nameof(request.FileName));
        }

        var success = await mediaLibrary.UpdateFileTags(request.Path, request.FileName, request.Tags);
        return success ? TypedResults.NoContent() : TypedResults.NotFound();
    }

    [ApiExplorerSettings(GroupName = "Media")]
    [Tags("Media")]
    [EndpointName("Search media library")]
    private static async Task<Results<Ok<IEnumerable<FileReference>>, UnauthorizedHttpResult>> Search(string? path,
        string? searchTerm,
        string? language,
        string[]? tags,
        IMediaLibraryService mediaLibraryService)
    {
        try
        {
            searchTerm ??= "";
            var result = await mediaLibraryService.Search(path, searchTerm, language, tags);
            return TypedResults.Ok(result);
        }
        catch (UnauthorizedAccessException)
        {
            return TypedResults.Unauthorized();
        }
    }

    [ApiExplorerSettings(GroupName = "Media")]
    [Tags("Media")]
    [EndpointName("Delete file")]
    private static async Task<Results<
        NoContent,
        UnauthorizedHttpResult,
        NotFound
    >> DeleteFile(string? path, string fileName,
        IMediaLibraryService mediaLibraryService)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(fileName))
            {
                return TypedResults.NotFound();
            }

            var deleted = await mediaLibraryService.DeleteFile(path, fileName);
            return deleted ? TypedResults.NoContent() : TypedResults.NotFound();
        }
        catch (UnauthorizedAccessException)
        {
            return TypedResults.Unauthorized();
        }
    }

    [ApiExplorerSettings(GroupName = "Media")]
    [Tags("Media")]
    [EndpointName("Delete folder")]
    private static async Task<Results<
        NoContent,
        UnauthorizedHttpResult,
        NotFound
    >> DeleteFolder(string? path, string folderName,
        IMediaLibraryService mediaLibraryService)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(folderName))
            {
                return TypedResults.NotFound();
            }

            var deleted = await mediaLibraryService.DeleteFolder(path, folderName);
            return deleted ? TypedResults.NoContent() : TypedResults.NotFound();
        }
        catch (UnauthorizedAccessException)
        {
            return TypedResults.Unauthorized();
        }
    }

    [ApiExplorerSettings(GroupName = "Media")]
    [Tags("Media")]
    [EndpointName("Create folder")]
    private static async
        Task<Results<Ok<FolderReference>, UnauthorizedHttpResult, BadRequest<Dictionary<string, string[]>>>>
        CreateFolder(
            string? path,
            string folderName,
            IMediaLibraryService mediaLibraryService)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(folderName))
            {
                return BadRequest("Field is required", nameof(folderName));
            }

            var folderRef = await mediaLibraryService.CreateFolder(path, folderName);
            return TypedResults.Ok(folderRef);
        }
        catch (UnauthorizedAccessException)
        {
            return TypedResults.Unauthorized();
        }
    }

    [ApiExplorerSettings(GroupName = "Media")]
    [Tags("Media")]
    [EndpointName("Download file")]
    private static async Task<Results<
        Ok,
        FileStreamHttpResult,
        NotFound,
        UnauthorizedHttpResult
    >> Download(
        string? path,
        string fileName,
        IMediaLibraryService mediaLibraryService)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(fileName))
            {
                return TypedResults.NotFound();
            }

            var stream = await mediaLibraryService.DownloadFile(path, fileName);
            if (stream == null)
            {
                return TypedResults.NotFound();
            }

            return TypedResults.File(stream, fileName);
        }
        catch (UnauthorizedAccessException)
        {
            return TypedResults.Unauthorized();
        }
    }

    [ApiExplorerSettings(GroupName = "Media")]
    [Tags("Media")]
    [EndpointName("Upload file")]
    private static async
        Task<Results<
            Ok<FileReference>,
            UnauthorizedHttpResult,
            BadRequest<Dictionary<string, string[]>>
        >> Upload(
            [FromForm] UploadRequest request,
            IFormFile file,
            [FromServices] IMediaLibraryService mediaLibraryService)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(request?.FileName))
            {
                return BadRequest("Field is required", nameof(request.FileName));
            }

            var fileRef = await mediaLibraryService.GetFile(request.Path, request.FileName);
            if (fileRef == null)
            {
                var model = new FileModel
                {
                    Path = request.Path,
                    Name = request.FileName,
                    Tags = request.Tags,
                    AltText = request.AltText,
                    Description = request.Description,
                    Title = request.Title,
                    Creator = request.Creator,
                    MetaData = request.MetaData
                };
                fileRef = await mediaLibraryService.CreateFile(model, file.OpenReadStream());
            }
            else
            {
                await mediaLibraryService.UpdateFile(request.Path, request.FileName, file.OpenReadStream());
            }

            return TypedResults.Ok(fileRef);
        }
        catch (UnauthorizedAccessException)
        {
            return TypedResults.Unauthorized();
        }
    }

    [ApiExplorerSettings(GroupName = "Media")]
    [Tags("Media")]
    [EndpointName("Localize file")]
    private static async Task<Results<NoContent, NotFound, UnauthorizedHttpResult>> LocalizeFile(
        [FromQuery, Required] string? path,
        [FromQuery, Required] string fileName,
        [FromQuery, Required] string language,
        [FromQuery, Required] string propertyName,
        [FromQuery] string value,
        IMediaLibraryService mediaLibraryService)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(fileName))
            {
                return TypedResults.NotFound();
            }

            var reference = await mediaLibraryService.GetFile(path, fileName, null);
            if (reference == null)
            {
                return TypedResults.NotFound();
            }

            await mediaLibraryService.LocalizeField(reference.Id, propertyName, language, value);
            return TypedResults.NoContent();
        }
        catch (UnauthorizedAccessException)
        {
            return TypedResults.Unauthorized();
        }
    }

    [ApiExplorerSettings(GroupName = "Media")]
    [Tags("Media")]
    [EndpointName("List folder contents")]
    private static async Task<Results<Ok<FolderContents>, UnauthorizedHttpResult>> List(string? path, string? language,
        IMediaLibraryService mediaLibraryService)
    {
        try
        {
            var contents = await mediaLibraryService.GetFolder(path, language);
            return TypedResults.Ok(contents);
        }
        catch (UnauthorizedAccessException)
        {
            return TypedResults.Unauthorized();
        }
    }
}