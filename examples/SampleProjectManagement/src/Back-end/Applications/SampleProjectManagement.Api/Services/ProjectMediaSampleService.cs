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

    public async Task<ProjectMediaDownload?> DownloadAsync(string? path, string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return null;

        using var result = await _mediaLibraryService.DownloadFileAsync(path, fileName);
        if (!result.Success || result.Data is null) return null;

        var stream = result.Data;
        result.Data = null;
        return new ProjectMediaDownload(stream, fileName, ResolveContentType(fileName));
    }

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

public sealed record ProjectMediaDownload(
    Stream Stream,
    string FileName,
    string ContentType);
