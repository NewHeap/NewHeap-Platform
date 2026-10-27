using Microsoft.AspNetCore.StaticFiles;
using NewHeap.Media;
using NewHeap.Media.EventHandlers;
using NewHeap.Media.Modules;
using NewHeap.Platform.Media.MediaStorage.S3Bucket;
using SampleProjectManagement.Api.Events;

namespace SampleProjectManagement.Api.Services;

public sealed class ProjectMediaSampleService
{
    private readonly IConfiguration _configuration;
    private readonly IMediaLibraryService _mediaLibraryService;
    private readonly IMediaStorage _mediaStorage;
    private readonly IFileStructureStorage _fileStructureStorage;
    private readonly IAuthorizationModule _authorizationModule;
    private readonly IThumbnailService _thumbnailService;
    private readonly IEnumerable<IHandleMediaLibraryEvent> _eventHandlers;
    private readonly NhMediaContext _mediaContext;
    private readonly SampleMediaEventLog _eventLog;
    private readonly SampleMediaThumbnailStore _thumbnailStore;
    private readonly SampleMediaAuthorizationLog _authorizationLog;

    public ProjectMediaSampleService(
        IConfiguration configuration,
        IMediaLibraryService mediaLibraryService,
        IMediaStorage mediaStorage,
        IFileStructureStorage fileStructureStorage,
        IAuthorizationModule authorizationModule,
        IThumbnailService thumbnailService,
        IEnumerable<IHandleMediaLibraryEvent> eventHandlers,
        NhMediaContext mediaContext,
        SampleMediaEventLog eventLog,
        SampleMediaThumbnailStore thumbnailStore,
        SampleMediaAuthorizationLog authorizationLog)
    {
        _configuration = configuration;
        _mediaLibraryService = mediaLibraryService;
        _mediaStorage = mediaStorage;
        _fileStructureStorage = fileStructureStorage;
        _authorizationModule = authorizationModule;
        _thumbnailService = thumbnailService;
        _eventHandlers = eventHandlers;
        _mediaContext = mediaContext;
        _eventLog = eventLog;
        _thumbnailStore = thumbnailStore;
        _authorizationLog = authorizationLog;
    }

    /// <summary>
    /// Opens a media file for the download endpoint. The stored file, not the requested name, decides the
    /// download name, the content type (<see cref="NhMediaContentTypes"/>, application/octet-stream for any
    /// name that is not on its list) and whether the file may be shown inline, which only raster images may.
    /// </summary>
    public async Task<ProjectMediaDownload?> DownloadAsync(string? path, string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return null;
        }

        using var result = await _mediaLibraryService.OpenDownloadAsync(path, fileName);
        if (!result.Success || result.Data is null)
        {
            return null;
        }

        // The controller writes the stream to the response, which disposes it.
        var download = result.Data;
        result.Data = null;
        return new ProjectMediaDownload(download.Content, download.FileName, download.ContentType, download.Inline);
    }

    /// <summary>
    /// Resolves every media file that one view references with a single batch lookup. Collect the ids first,
    /// for example while mapping content blocks, and resolve them together instead of calling
    /// <see cref="IMediaLibraryService.GetFileAsync(Guid)"/> per block: relational storage then needs one
    /// file query and one folder query for the whole view.
    /// </summary>
    /// <remarks>
    /// Id lookups do not call the path-based <see cref="IAuthorizationModule"/>. Only resolve ids that
    /// content the caller may already see refers to.
    /// </remarks>
    public async Task<ProjectMediaFileLookup> ResolveFilesAsync(IReadOnlyCollection<Guid> fileIds)
    {
        var files = await _mediaLibraryService.GetFilesAsync(fileIds);

        var resolved = new List<ProjectMediaFileSummary>();
        var missingIds = new List<Guid>();
        foreach (var id in fileIds.Distinct())
        {
            if (files.TryGetValue(id, out var file))
            {
                resolved.Add(new ProjectMediaFileSummary(
                    file.Id,
                    file.Name,
                    file.Folder.FullPath,
                    file.Folder.Id,
                    file.Title,
                    file.AltText,
                    file.Thumbnail));
            }
            else
            {
                missingIds.Add(id);
            }
        }

        return new ProjectMediaFileLookup(resolved, missingIds);
    }

    /// <summary>
    /// The media type the assistant reports for a media file without a stored media type. Downloads do not
    /// use it: they are served with the library's <see cref="NhMediaContentTypes"/>.
    /// </summary>
    public static string ResolveContentType(string fileName)
    {
        var provider = new FileExtensionContentTypeProvider();
        return provider.TryGetContentType(fileName, out var contentType)
            ? contentType
            : "application/octet-stream";
    }

    public ProjectMediaDiagnostics GetDiagnostics()
    {
        var s3Settings = new S3MediaStorageSettings();
        _configuration.GetSection("NewHeap:MediaLibrary:S3Settings").Bind(s3Settings);
        var s3Errors = ValidateS3Settings(s3Settings);

        return new ProjectMediaDiagnostics(
            _mediaStorage.GetType().FullName ?? _mediaStorage.GetType().Name,
            _fileStructureStorage.GetType().FullName ?? _fileStructureStorage.GetType().Name,
            _authorizationModule.GetType().FullName ?? _authorizationModule.GetType().Name,
            _thumbnailService.GetType().FullName ?? _thumbnailService.GetType().Name,
            _eventHandlers.Select(handler => handler.GetType().FullName ?? handler.GetType().Name).Order().ToArray(),
            _mediaContext.Values.ToDictionary(item => item.Key, item => item.Value?.ToString()),
            new ProjectMediaS3Diagnostics(
                s3Errors.Count == 0,
                s3Settings.BucketName ?? string.Empty,
                s3Settings.RegionEndpoint?.SystemName ?? string.Empty,
                Redact(s3Settings.AccessKey),
                Redact(s3Settings.SecretKey),
                s3Errors),
            _thumbnailStore.Count,
            _eventLog.Events,
            _authorizationLog.Items);
    }

    public static IReadOnlyList<string> ValidateS3Settings(S3MediaStorageSettings settings)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(settings.BucketName)) errors.Add("BucketName is required.");
        if (settings.RegionEndpoint is null || string.IsNullOrWhiteSpace(settings.RegionEndpoint.SystemName))
            errors.Add("RegionEndpoint is required.");
        if (string.IsNullOrWhiteSpace(settings.AccessKey)) errors.Add("AccessKey is required.");
        if (string.IsNullOrWhiteSpace(settings.SecretKey)) errors.Add("SecretKey is required.");
        return errors;
    }

    public static string Redact(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "<not-configured>" : "***configured***";
}

public sealed record ProjectMediaDiagnostics(
    string MediaStorage,
    string FileStructureStorage,
    string AuthorizationModule,
    string ThumbnailService,
    IReadOnlyList<string> EventHandlers,
    IReadOnlyDictionary<string, string?> ContextValues,
    ProjectMediaS3Diagnostics S3,
    int ThumbnailCount,
    IReadOnlyList<SampleMediaEvent> RecentEvents,
    IReadOnlyList<SampleMediaAuthorizationDecision> RecentAuthorizationDecisions);

public sealed record ProjectMediaS3Diagnostics(
    bool Valid,
    string BucketName,
    string Region,
    string AccessKey,
    string SecretKey,
    IReadOnlyList<string> ValidationErrors);

public sealed record ProjectMediaFileLookup(
    IReadOnlyList<ProjectMediaFileSummary> Files,
    IReadOnlyList<Guid> MissingIds);

public sealed record ProjectMediaFileSummary(
    Guid Id,
    string Name,
    string FolderPath,
    Guid? FolderId,
    string? Title,
    string? AltText,
    string? Thumbnail);

public sealed record ProjectMediaDownload(
    Stream Stream,
    string FileName,
    string ContentType,
    bool Inline);
