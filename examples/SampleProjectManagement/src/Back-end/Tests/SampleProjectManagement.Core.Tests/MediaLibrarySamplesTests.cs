using Amazon;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using NewHeap.Media;
using NewHeap.Media.EventHandlers;
using NewHeap.Media.Modules;
using NewHeap.Platform.Media.MediaStorage.FileSystem;
using NewHeap.Platform.Media.MediaStorage.S3Bucket;
using SampleProjectManagement.Api.Events;
using SampleProjectManagement.Api.Services;
using System.Text;
using Xunit;

namespace SampleProjectManagement.Core.Tests;

public class MediaLibrarySamplesTests
{
    [Fact]
    public void CompositionRootRegistersConcreteMediaModules()
    {
        var storagePath = CreateTempDirectory();
        try
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddHttpContextAccessor();
            services.AddSingleton<SampleMediaEventLog>();
            services.AddSingleton<SampleMediaThumbnailStore>();
            services.AddSingleton<SampleMediaAuthorizationLog>();
            services.AddNhMedia(media =>
            {
                media.UsePostgreSqlFileStructureStorage(
                    "Host=localhost;Database=sample-media-registration;Username=postgres;Password=postgres",
                    options => options.RunMigrations = false);
                media.UseFileSystemMediaStorage(storagePath, true);
                media.AddAuthentication<ProjectMediaAuthorizationModule>();
                media.AddThumbnailService<ProjectMediaThumbnailService>();
                media.AddEventHandler<ProjectMediaEventHandler>();
            });

            using var provider = services.BuildServiceProvider(new ServiceProviderOptions
            {
                ValidateOnBuild = true,
                ValidateScopes = true
            });
            using var scope = provider.CreateScope();

            Assert.IsType<DefaultMediaStorage>(scope.ServiceProvider.GetRequiredService<IMediaStorage>());
            Assert.Contains("PostgreSqlFileStructureStorage",
                scope.ServiceProvider.GetRequiredService<IFileStructureStorage>().GetType().Name);
            Assert.IsType<ProjectMediaAuthorizationModule>(
                scope.ServiceProvider.GetRequiredService<IAuthorizationModule>());
            Assert.IsType<ProjectMediaThumbnailService>(
                scope.ServiceProvider.GetRequiredService<IThumbnailService>());
            Assert.Contains(
                scope.ServiceProvider.GetServices<IHandleMediaLibraryEvent>(),
                handler => handler is ProjectMediaEventHandler);
        }
        finally
        {
            Directory.Delete(storagePath, recursive: true);
        }
    }

    [Fact]
    public async Task FileSystemStorageRoundTripsAndDeletesBinaryContent()
    {
        var storagePath = CreateTempDirectory();
        try
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddMediaFileSystemStorage(storagePath, true);
            using var provider = services.BuildServiceProvider();
            using var scope = provider.CreateScope();
            var storage = scope.ServiceProvider.GetRequiredService<IMediaStorage>();

            await using var original = new MemoryStream(Encoding.UTF8.GetBytes("project document v1"));
            var id = await storage.SaveFileAsync(original);
            await using (var stored = await storage.GetFileAsync(id))
            {
                Assert.NotNull(stored);
                using var reader = new StreamReader(stored!, Encoding.UTF8);
                Assert.Equal("project document v1", await reader.ReadToEndAsync());
            }

            await using var updated = new MemoryStream(Encoding.UTF8.GetBytes("project document v2"));
            Assert.True((await storage.UpdateFileAsync(updated, id)).Success);
            Assert.True((await storage.DeleteAsync(id)).Success);
            Assert.Null(await storage.GetFileAsync(id));
        }
        finally
        {
            Directory.Delete(storagePath, recursive: true);
        }
    }

    [Fact]
    public void S3ProviderRegistersAndDiagnosticsNeverExposeCredentials()
    {
        var services = new ServiceCollection();
        services.AddMediaS3BucketStorage(settings =>
        {
            settings.BucketName = "sample-project-documents";
            settings.RegionEndpoint = RegionEndpoint.EUCentral1;
            settings.AccessKey = "sample-access-key";
            settings.SecretKey = "sample-secret-key";
        });

        using var provider = services.BuildServiceProvider();
        Assert.IsType<S3BucketStorage>(provider.GetRequiredService<IMediaStorage>());

        var settings = new S3MediaStorageSettings
        {
            BucketName = "sample-project-documents",
            RegionEndpoint = RegionEndpoint.EUCentral1,
            AccessKey = "sample-access-key",
            SecretKey = "sample-secret-key"
        };
        Assert.Empty(ProjectMediaSampleService.ValidateS3Settings(settings));
        Assert.Equal("***configured***", ProjectMediaSampleService.Redact(settings.AccessKey));
        Assert.DoesNotContain("sample-access-key", ProjectMediaSampleService.Redact(settings.AccessKey));
    }

    [Theory]
    [InlineData("planning.pdf", "application/pdf")]
    [InlineData("project-data.unknown", "application/octet-stream")]
    public void SampleDownloadAdapterReturnsAValidContentType(string fileName, string expectedContentType)
    {
        Assert.Equal(expectedContentType, ProjectMediaSampleService.ResolveContentType(fileName));
    }

    [Fact]
    public async Task ActiveDivisionAuthorizationRequiresScopedPathAndPermission()
    {
        var divisionId = Guid.NewGuid();
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers[NewHeap.Platform.AspNet.Common.Constants.HttpHeaderKeys.ActiveDivisionId] =
            divisionId.ToString();
        var module = new ProjectMediaAuthorizationModule(
            new HttpContextAccessor { HttpContext = httpContext },
            new SampleMediaAuthorizationLog());

        var denied = CreateAuthorizationContext(
            $"/divisions/{divisionId:D}/projects",
            ActionType.Update);
        await module.IsAuthorizedAsync(denied);
        Assert.False(denied.Authorized);

        httpContext.Request.Headers[ProjectMediaAuthorizationModule.SamplePermissionsHeader] =
            "app.project.view,app.project.manage";
        var allowed = CreateAuthorizationContext(
            $"/divisions/{divisionId:D}/projects/documents",
            ActionType.Update);
        await module.IsAuthorizedAsync(allowed);
        Assert.True(allowed.Authorized);

        var wrongDivision = CreateAuthorizationContext(
            $"/divisions/{Guid.NewGuid():D}/projects",
            ActionType.Read);
        await module.IsAuthorizedAsync(wrongDivision);
        Assert.False(wrongDivision.Authorized);
    }

    [Fact]
    public async Task ThumbnailAndEventHandlersObserveCreateAndCleanup()
    {
        var id = Guid.NewGuid();
        var store = new SampleMediaThumbnailStore();
        var thumbnails = new ProjectMediaThumbnailService(store);
        var file = new FileReference
        {
            Id = id,
            Name = "planning.pdf",
            Folder = new FolderReference { Name = "documents", Path = "/", FullPath = "/documents" }
        };

        await thumbnails.UpdateThumbnailAsync(file);
        Assert.StartsWith("data:image/svg+xml,", await thumbnails.GetThumbnailAsync(id));

        var removed = CreateFileEvent(id, file, MediaLibraryFileEventType.Removed);
        await thumbnails.HandleEvent(removed);
        Assert.Null(await thumbnails.GetThumbnailAsync(id));

        var eventLog = new SampleMediaEventLog();
        var handler = new ProjectMediaEventHandler(
            eventLog,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ProjectMediaEventHandler>.Instance);
        await handler.HandleEvent(CreateFileEvent(id, file, MediaLibraryFileEventType.Added));
        Assert.Contains(eventLog.Events, item =>
            item.ResourceType == "file" && item.EventType == nameof(MediaLibraryFileEventType.Added));
    }

    private static AuthorizationContext CreateAuthorizationContext(string path, ActionType action)
    {
        var context = (AuthorizationContext)Activator.CreateInstance(
            typeof(AuthorizationContext),
            nonPublic: true)!;
        context.Path = path;
        context.Action = action;
        return context;
    }

    private static MediaLibraryFileEvent CreateFileEvent(
        Guid id,
        FileReference file,
        MediaLibraryFileEventType type)
    {
        var @event = (MediaLibraryFileEvent)Activator.CreateInstance(
            typeof(MediaLibraryFileEvent),
            nonPublic: true)!;
        typeof(MediaLibraryFileEvent).GetProperty(nameof(MediaLibraryFileEvent.Id))!.SetValue(@event, id);
        typeof(MediaLibraryFileEvent).GetProperty(nameof(MediaLibraryFileEvent.OldFile))!.SetValue(@event, file);
        typeof(MediaLibraryFileEvent).GetProperty(nameof(MediaLibraryFileEvent.NewFile))!.SetValue(@event, file);
        typeof(MediaLibraryFileEvent).GetProperty(nameof(MediaLibraryFileEvent.Type))!.SetValue(@event, type);
        return @event;
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "SampleProjectManagement.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
