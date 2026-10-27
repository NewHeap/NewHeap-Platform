using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.ObjectPool;
using Microsoft.Net.Http.Headers;
using NewHeap.Media.Models;
using NhMedia.Http;
using NewHeap.Media.Modules;
using NewHeap.Platform.Common.Models;
using NhMedia.Http.Models;
using System.Text.Json;

// ReSharper disable once CheckNamespace
namespace NewHeap.Media;

public static class WebApplicationExtensions
{
    private static readonly JsonSerializerOptions _jsonOptions = new JsonSerializerOptions()
    {
        PropertyNameCaseInsensitive = true,
        
    };
    /// <summary>
    /// Configure Media endpoints on /media using JWT bearer auth
    /// </summary>
    /// <param name="app"></param>
    /// <returns></returns>
    public static RouteGroupBuilder MapNhMediaEndpoints(this IEndpointRouteBuilder app)
    {
        return MapNhMediaEndpoints(app,"media", opt =>
        {
            opt.ConfigureAllRoutes(builder => builder.RequireAuthorization(p =>
            {
                p.AddAuthenticationSchemes("Bearer");
                p.RequireAuthenticatedUser();
            }));
        });
    }

    /// <summary>
    /// Configure media endpoints on /media
    /// </summary>
    /// <param name="app"></param>
    /// <param name="configureOptions"></param>
    /// <returns></returns>
    public static RouteGroupBuilder MapNhMediaEndpoints(this IEndpointRouteBuilder app, Action<MediaLibraryRouteBuilder>? configureOptions)
    {
        return MapNhMediaEndpoints(app,"media", configureOptions);
    }
    
    /// <summary>
    /// Configure media endpoints
    /// </summary>
    /// <param name="app"></param>
    /// <param name="prefix">Prefix for media library endpoints</param>
    /// <param name="configureOptions">Configure endpoint options</param>
    /// <returns></returns>
    public static RouteGroupBuilder MapNhMediaEndpoints(this IEndpointRouteBuilder app, string prefix, Action<MediaLibraryRouteBuilder>? configureOptions)
    {
        if (app.ServiceProvider.GetService(typeof(NhMediaContext)) is NhMediaContext context)
        {
            context.Values[NhMediaHttpConstants.API_PREFIX_CONTEXT_KEY] = prefix;
        }
        // The group joins the application's endpoints at the end, built once for every reader.
        var builtOnce = new NhEndpointsBuiltOnce(app);
        var group = builtOnce.MapGroup(prefix);
        group.AddEndpointFilter<MediaContextEndpointFilter>();

        var options = new MediaLibraryRouteBuilder();
        configureOptions?.Invoke(options);

        var list = group.MapGet("list", List).WithDescription("List files and folders");
        list = options.AllRoutesAction?.Invoke(list) ?? list;
        options.ListAction?.Invoke(list);

        var download = group.MapGet("download", Download)
            .WithDescription("Download a file. The content type comes from the stored file name through a fixed list " +
                             "(application/octet-stream for any other name); raster images are served inline, every " +
                             "other file as an attachment, always with X-Content-Type-Options: nosniff.");
        options.AllRoutesAction?.Invoke(download);
        options.DownloadAction?.Invoke(download);

        var search = group.MapGet("search", Search).WithDescription("Search files");
        options.AllRoutesAction?.Invoke(search);
        options.SearchAction?.Invoke(search);

        var upload = group.MapPost("upload", Upload).DisableAntiforgery().WithDescription("Upload a file");
        options.AllRoutesAction?.Invoke(upload);
        options.UploadFileAction?.Invoke(upload);

        var createFolder = group.MapPost("folder", CreateFolder).WithDescription("Create a folder");
        options.AllRoutesAction?.Invoke(createFolder);
        options.CreateFolderAction?.Invoke(createFolder);
        
        var localizeFile = group.MapPost("file/localize", LocalizeFile).WithDescription("Add localization to a file");
        options.AllRoutesAction?.Invoke(localizeFile);
        options.LocalizeFileAction?.Invoke(localizeFile);
        
        var setTags = group.MapPost("file/tags", UpdateTags).WithDescription("Update file tags");
        options.AllRoutesAction?.Invoke(setTags);
        options.UpdateTagsAction?.Invoke(setTags);

        var updateFile = group.MapPut("file/{id:guid}", UpdateFile).WithDescription("Update file (meta)data");
        options.AllRoutesAction?.Invoke(updateFile);
        options.UpdateFileAction?.Invoke(updateFile);
        
        var updateFolder = group.MapPut("folder", UpdateFolder).WithDescription("Update folder properties");
        options.AllRoutesAction?.Invoke(updateFolder);
        options.UpdateFolderAction?.Invoke(updateFolder);

        var deleteFolder = group.MapDelete("folder", DeleteFolder).WithDescription("Delete a folder and all files and subfolders");
        options.AllRoutesAction?.Invoke(deleteFolder);
        options.DeleteFolderAction?.Invoke(deleteFolder);
        
        var deleteFile = group.MapDelete("file", DeleteFile).WithDescription("Delete a file");
        options.AllRoutesAction?.Invoke(deleteFile);
        options.DeleteFileAction?.Invoke(deleteFile);

        builtOnce.AddTo(app);
        return group;
    }

    private static BadRequest<Dictionary<string, string[]>> BadRequest(string error, string field = "")
    {
        return TypedResults.BadRequest(new Dictionary<string, string[]> { { field, [error] } });
    }
    
    private static BadRequest<Dictionary<string, string[]>> BadRequest(TaskResult result)
    {
        return TypedResults.BadRequest(GetErrors(result));
    }

    private static Conflict<Dictionary<string, string[]>> Conflict(TaskResult result)
    {
        return TypedResults.Conflict(GetErrors(result));
    }

    private static Dictionary<string, string[]> GetErrors(TaskResult result)
    {
        var dict = new Dictionary<string, string[]>();
        foreach (var item in result.GetResultItems())
        {
            dict[item.Name] = item.ErrorMessages.Select(x => x.ToString()).ToArray();
        }

        return dict;
    }

    private static bool HasError(TaskResult result, string errorCode)
    {
        return result.GetResultItems()
            .SelectMany(item => item.ErrorMessages)
            .Any(message => string.Equals(message.ToString(), errorCode, StringComparison.Ordinal));
    }

    [ApiExplorerSettings(GroupName = "Media")]
    [Tags("Media")]
    [EndpointName("Update folder information")]
    private static async Task<Results<
        Ok<FolderReference>,
        NotFound,
        UnauthorizedHttpResult,
        Conflict<Dictionary<string, string[]>>,
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

        try
        {
            var result = await mediaLibrary.UpdateFolderAsync(request.Path, request.FolderName, request.NewPath,
                request.NewName);
            if (result.Success)
            {
                return TypedResults.Ok(result.Data);
            }

            if (HasError(result, NhMediaErrorCodes.FolderNameExists))
            {
                return Conflict(result);
            }

            return TypedResults.NotFound();
        }
        catch (UnauthorizedAccessException)
        {
            return TypedResults.Unauthorized();
        }
    }

    [ApiExplorerSettings(GroupName = "Media")]
    [Tags("Media")]
    [EndpointName("Update file information")]
    private static async Task<Results<
        Ok<FileReference>,
        NotFound,
        UnauthorizedHttpResult,
        Conflict<Dictionary<string, string[]>>
    >> UpdateFile([FromRoute] Guid id,
        [FromBody] FileModel model,
        [FromServices] IMediaLibraryService mediaLibrary)
    {
        try
        {
            var result = await mediaLibrary.UpdateFileAsync(id, model);
            if (result.Success)
            {
                var reference = await mediaLibrary.GetFileAsync(id);
                return TypedResults.Ok(reference.Data);
            }

            if (HasError(result, NhMediaErrorCodes.FileNameExists))
            {
                return Conflict(result);
            }

            return TypedResults.NotFound();
        }
        catch (UnauthorizedAccessException)
        {
            return TypedResults.Unauthorized();
        }
    }

    [ApiExplorerSettings(GroupName = "Media")]
    [Tags("Media")]
    [EndpointName("Update file tags")]
    private static async Task<Results<
        NoContent,
        NotFound,
        UnauthorizedHttpResult,
        BadRequest<Dictionary<string, string[]>>
    >> UpdateTags(
        [FromBody] UpdateTagsRequest request,
        [FromServices] IMediaLibraryService mediaLibrary
    )
    {
        if (string.IsNullOrWhiteSpace(request?.FileName))
        {
            return BadRequest("Field is required", nameof(request.FileName));
        }

        try
        {
            var result = await mediaLibrary.UpdateFileTagsAsync(request.Path, request.FileName, request.Tags);
            return result.Success ? TypedResults.NoContent() : TypedResults.NotFound();
        }
        catch (UnauthorizedAccessException)
        {
            return TypedResults.Unauthorized();
        }
    }

    [ApiExplorerSettings(GroupName = "Media")]
    [Tags("Media")]
    [EndpointName("Search media library")]
    private static async Task<Results<Ok<SearchResults>, UnauthorizedHttpResult>> Search(string? path,
        string? searchTerm,
        string? language,
        string[]? tags,
        string[]? includeExtensions,
        string[]? excludeExtensions,
        int? pageIndex,
        int? pageSize,
        IMediaLibraryService mediaLibraryService)
    {
        try
        {
            searchTerm ??= "";

            var options = new SearchOptions
            {
                Language = language,
                ExcludedExtensions = excludeExtensions,
                IncludedExtensions = includeExtensions,
                Tags = tags,
                PageIndex = pageIndex ?? 0,
                PageSize = pageSize ?? 20,
            };

            options.IncludedExtensions = ProcessExtensions(options.IncludedExtensions); 
            options.ExcludedExtensions = ProcessExtensions(options.ExcludedExtensions);
            
            var result = await mediaLibraryService.SearchAsync(path, searchTerm, options);
            return TypedResults.Ok(result);
        }
        catch (UnauthorizedAccessException)
        {
            return TypedResults.Unauthorized();
        }

        string[]? ProcessExtensions(string[]? extensions)
        {
            if (extensions == null)
            {
                return extensions;
            }
            var result = new List<string>();
            foreach (var extension in extensions)
            {
                var items = extension.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                result.AddRange(items);
            }
            return result.ToArray();
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

            var deleted = await mediaLibraryService.DeleteFileAsync(path, fileName);
            return deleted.Success ? TypedResults.NoContent() : TypedResults.NotFound();
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

            var deleted = await mediaLibraryService.DeleteFolderAsync(path, folderName);
            return deleted.Success ? TypedResults.NoContent() : TypedResults.NotFound();
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

            var result = await mediaLibraryService.CreateFolderAsync(path, folderName);
            if (!result.Success)
            {
                return BadRequest(result);
            }
            return TypedResults.Ok(result.Data);
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
        IMediaLibraryService mediaLibraryService,
        HttpContext httpContext)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(fileName))
            {
                return TypedResults.NotFound();
            }

            var download = await mediaLibraryService.OpenDownloadAsync(path, fileName);
            if (!download.Success)
            {
                return TypedResults.NotFound();
            }

            // The stored name chooses the type from a fixed list; nosniff keeps a browser from guessing
            // another one from the bytes, and every type but a raster image is saved instead of rendered.
            var file = download.Data;
            httpContext.Response.Headers.XContentTypeOptions = "nosniff";
            if (!file.Inline)
            {
                return TypedResults.File(file.Content, file.ContentType, file.FileName);
            }

            var disposition = new ContentDispositionHeaderValue("inline");
            disposition.SetHttpFileName(file.FileName);
            httpContext.Response.Headers.ContentDisposition = disposition.ToString();
            return TypedResults.File(file.Content, file.ContentType);
        }
        catch (UnauthorizedAccessException)
        {
            return TypedResults.Unauthorized();
        }
    }

    [ApiExplorerSettings(GroupName = "Media")]
    [Tags("Media")]
    [RequestFormLimits(MultipartBodyLengthLimit = long.MaxValue)]
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

            var fileRef = await mediaLibraryService.GetFileAsync(request.Path, request.FileName);
            await using var fileStream = file.OpenReadStream();
            if (!fileRef.Success)
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
                fileRef = await mediaLibraryService.CreateFileAsync(model, fileStream);
            }
            else
            {
                var update = await mediaLibraryService.UpdateFileAsync(request.Path, request.FileName, fileStream);
                if (!update.Success)
                {
                    fileRef = TaskResult<FileReference>.Failed(update);
                }
            }

            if (!fileRef.Success)
            {
                return BadRequest(fileRef);
            }

            return TypedResults.Ok(fileRef.Data);
        }
        catch (UnauthorizedAccessException)
        {
            return TypedResults.Unauthorized();
        }
    }

    [ApiExplorerSettings(GroupName = "Media")]
    [Tags("Media")]
    [EndpointName("Localize file")]
    private static async Task<Results<
        NoContent,
        NotFound,
        UnauthorizedHttpResult,
        BadRequest<Dictionary<string, string[]>>
    >> LocalizeFile(
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

            var reference = await mediaLibraryService.GetFileAsync(path, fileName, null);
            if (!reference.Success)
            {
                return TypedResults.NotFound();
            }

            var result = await mediaLibraryService.LocalizeFieldAsync(reference.Data.Id, propertyName, language,
                value);
            if (result.Success)
            {
                return TypedResults.NoContent();
            }

            if (HasError(result, NhMediaErrorCodes.PropertyNotLocalizable))
            {
                return BadRequest(result);
            }

            return TypedResults.NotFound();
        }
        catch (UnauthorizedAccessException)
        {
            return TypedResults.Unauthorized();
        }
    }

    [ApiExplorerSettings(GroupName = "Media")]
    [Tags("Media")]
    [EndpointName("List folder contents")]
    private static async Task<Results<Ok<FolderContents>, UnauthorizedHttpResult>> List(
        string? path, 
        string? language,
        string? orderBy,
        IMediaLibraryService mediaLibraryService,
        ILoggerFactory loggerFactory,
        [FromQuery] int page = 0,
        [FromQuery] int pageSize = 30
        )
    {
        try
        {
            FileGetOptions? options = new FileGetOptions
            {
                Page = page,
                PageSize = pageSize
            };
            if (!string.IsNullOrWhiteSpace(orderBy))
            {
                try
                {
                    var sorts = JsonSerializer.Deserialize<SortOption[]>(orderBy, _jsonOptions);
                    options.OrderBy = sorts?.ToList() ?? [];
                }
                catch (JsonException exception)
                {
                    loggerFactory
                        .CreateLogger(typeof(WebApplicationExtensions).FullName!)
                        .LogDebug(exception, "Ignored an invalid media sort expression");
                }
            }
            var contents = await mediaLibraryService.GetFolder(path, language, options);
            return TypedResults.Ok(contents);
        }
        catch (UnauthorizedAccessException)
        {
            return TypedResults.Unauthorized();
        }
    }
}
