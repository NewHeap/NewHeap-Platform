using Amazon;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NewHeap.Media;
using NewHeap.Media.EventHandlers;
using NewHeap.Media.FileStructureStorage.SqlServer;
using NewHeap.Media.Modules;
using NewHeap.Platform.Common.Identity.Claims;
using NewHeap.Platform.Common.Models;
using NewHeap.Platform.Media.MediaStorage.FileSystem;
using NewHeap.Platform.Media.MediaStorage.S3Bucket;
using SampleProjectManagement.Api.Authorization;
using SampleProjectManagement.Api.Composition;
using SampleProjectManagement.Api.Controllers;
using SampleProjectManagement.Api.Events;
using SampleProjectManagement.Api.Services;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using Xunit;

namespace SampleProjectManagement.Core.Tests;

public class MediaLibrarySamplesTests(SamplePostgreSqlServer postgreSql)
{
    /// <summary>
    /// A request header that claims media permissions. The tests send it to prove that no request
    /// header grants a media permission.
    /// </summary>
    private const string ClientPermissionsHeader = "X-Sample-Media-Permissions";

    [Fact]
    public async Task CompositionRootRegistersConcreteMediaModules()
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
                    options =>
                    {
                        options.Scheme = "project_media";
                        options.RunMigrations = false;
                    });
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
            var context = scope.ServiceProvider.GetRequiredService<FileStructureDbContext>();
            Assert.Equal("project_media", context.Model.GetDefaultSchema());
            var script = context.GetService<IMigrator>().GenerateScript();
            Assert.Contains("UPDATE project_media.\"Files\"", script);
            Assert.DoesNotContain("\"nhmedia\".", script);
            Assert.DoesNotContain("nhmedia.", script);
            Assert.Contains("PostgreSqlFileStructureStorage",
                scope.ServiceProvider.GetRequiredService<IFileStructureStorage>().GetType().Name);
            Assert.IsType<ProjectMediaAuthorizationModule>(
                scope.ServiceProvider.GetRequiredService<IAuthorizationModule>());
            Assert.IsType<ProjectMediaThumbnailService>(
                scope.ServiceProvider.GetRequiredService<IThumbnailService>());
            Assert.Contains(
                scope.ServiceProvider.GetServices<IHandleMediaLibraryEvent>(),
                handler => handler is ProjectMediaEventHandler);

            var divisionId = Guid.NewGuid();
            var httpContext = CreateMediaRequest(divisionId, SignedIn("app.project.manage"));
            scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>().HttpContext = httpContext;
            var media = scope.ServiceProvider.GetRequiredService<IMediaLibraryService>();
            var path = $"/divisions/{divisionId:D}/projects";

            TaskResult<FolderReference> invalidFolder = await media.CreateFolderAsync(path, " / ");
            Assert.False(invalidFolder.Success);
            Assert.NotEmpty(invalidFolder.GetResultItems());
            Assert.False((await media.UpdateFolderAsync(path, "documents", path, " / ")).Success);
            Assert.Empty(scope.ServiceProvider.GetRequiredService<SampleMediaEventLog>().Events);
        }
        finally
        {
            Directory.Delete(storagePath, recursive: true);
        }
    }

    [Fact]
    public async Task ReferencedFilesResolveWithOneBatchLookupOnPostgreSql()
    {
        var database = await postgreSql.CreateDatabaseAsync("media_sample");
        var storagePath = CreateTempDirectory();
        try
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddHttpContextAccessor();
            services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
            services.AddSingleton<SampleMediaEventLog>();
            services.AddSingleton<SampleMediaThumbnailStore>();
            services.AddSingleton<SampleMediaAuthorizationLog>();
            services.AddNhMedia(media =>
            {
                media.UsePostgreSqlFileStructureStorage(database, options =>
                {
                    options.Scheme = "project_media";
                    options.RunMigrations = false;
                });
                media.UseFileSystemMediaStorage(storagePath, true);
                media.AddAuthentication<ProjectMediaAuthorizationModule>();
                media.AddThumbnailService<ProjectMediaThumbnailService>();
                media.AddEventHandler<ProjectMediaEventHandler>();
            });
            services.AddScoped<ProjectMediaSampleService>();

            await using var provider = services.BuildServiceProvider();
            await using var scope = provider.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<FileStructureDbContext>().Database
                .MigrateAsync(TestContext.Current.CancellationToken);

            var divisionId = Guid.NewGuid();
            var httpContext = CreateMediaRequest(divisionId, SignedIn("app.project.manage"));
            scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>().HttpContext = httpContext;

            var media = scope.ServiceProvider.GetRequiredService<IMediaLibraryService>();
            var projectRoot = $"/divisions/{divisionId:D}/projects";
            var cover = await CreateFileAsync(media, $"{projectRoot}/launch", "cover.png");
            var plan = await CreateFileAsync(media, $"{projectRoot}/planning", "plan.pdf");
            var missingId = Guid.NewGuid();

            var lookup = await scope.ServiceProvider.GetRequiredService<ProjectMediaSampleService>()
                .ResolveFilesAsync([plan.Id, cover.Id, missingId, plan.Id]);

            Assert.Equal([plan.Id, cover.Id], lookup.Files.Select(file => file.Id));
            Assert.Equal([missingId], lookup.MissingIds);
            var resolvedCover = lookup.Files[1];
            Assert.Equal("cover.png", resolvedCover.Name);
            Assert.Equal($"{projectRoot}/launch", resolvedCover.FolderPath);
            Assert.NotNull(resolvedCover.FolderId);
            Assert.Equal(cover.Folder.Id, resolvedCover.FolderId);
            Assert.StartsWith("data:image/svg+xml,", resolvedCover.Thumbnail);
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
        services.AddLogging();
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

    [Theory]
    [InlineData(ActionType.Read)]
    [InlineData(ActionType.Create)]
    [InlineData(ActionType.Update)]
    [InlineData(ActionType.Delete)]
    public async Task RequestHeadersNeverGrantAMediaPermission(ActionType action)
    {
        var north = SampleAuthorizationDefaults.NorthDivisionId;
        var path = $"/divisions/{north:D}/projects/documents";

        var anonymous = CreateMediaRequest(north, new ClaimsPrincipal(new ClaimsIdentity()));
        anonymous.Request.Headers[ClientPermissionsHeader] = "app.project.view,app.project.manage";
        var signedInWithoutRights = CreateMediaRequest(north, SignedIn(), UserStore());
        signedInWithoutRights.Request.Headers[ClientPermissionsHeader] = "app.project.view,app.project.manage";

        Assert.Equal(
            ProjectMediaAuthorizationModule.NoPermissionSource,
            await AuthorizeAsync(anonymous, path, action));
        Assert.Equal(
            ProjectMediaAuthorizationModule.NoPermissionSource,
            await AuthorizeAsync(signedInWithoutRights, path, action));
    }

    [Fact]
    public async Task ApplicationPermissionAuthorizesTheProjectsOfTheActiveDivision()
    {
        var north = SampleAuthorizationDefaults.NorthDivisionId;
        var south = SampleAuthorizationDefaults.SouthDivisionId;
        var viewer = CreateMediaRequest(north, SignedIn("app.project.view"), UserStore());
        var manager = CreateMediaRequest(
            north,
            SignedIn("app.project.view", "app.project.manage"),
            UserStore());

        Assert.Equal(
            ProjectMediaAuthorizationModule.ApplicationPermissionSource,
            await AuthorizeAsync(viewer, $"/divisions/{north:D}/projects/documents", ActionType.Read));
        Assert.Equal(
            ProjectMediaAuthorizationModule.NoPermissionSource,
            await AuthorizeAsync(viewer, $"/divisions/{north:D}/projects/documents", ActionType.Update));
        Assert.Equal(
            ProjectMediaAuthorizationModule.ApplicationPermissionSource,
            await AuthorizeAsync(manager, $"/divisions/{north:D}/projects/documents", ActionType.Delete));

        // The path must lie in the projects folder of the active division.
        Assert.Equal(
            ProjectMediaAuthorizationModule.NoPermissionSource,
            await AuthorizeAsync(manager, $"/divisions/{south:D}/projects/documents", ActionType.Read));
        Assert.Equal(
            ProjectMediaAuthorizationModule.NoPermissionSource,
            await AuthorizeAsync(manager, $"/divisions/{north:D}/settings", ActionType.Read));
        Assert.Equal(
            ProjectMediaAuthorizationModule.NoPermissionSource,
            await AuthorizeAsync(manager, $"/divisions/{north:D}/projects-archive", ActionType.Read));
    }

    [Fact]
    public async Task DivisionPermissionIsReadFromTheUserStoreForThePathsDivision()
    {
        var north = SampleAuthorizationDefaults.NorthDivisionId;
        var south = SampleAuthorizationDefaults.SouthDivisionId;
        var userId = Guid.NewGuid();
        var store = UserStore(new SampleDivisionPermissionStore()
            .Grant(userId, north, "project.view", "project.manage"));

        // The token also names a South division permission that the user store does not grant.
        var member = SignedIn(
            userId,
            new Claim(NhPlatformClaimTypes.DivisionPermission, $"{south:D}_project.manage"));
        var inNorth = CreateMediaRequest(north, member, store);
        var inSouth = CreateMediaRequest(south, member, store);

        Assert.Equal(
            ProjectMediaAuthorizationModule.DivisionPermissionSource,
            await AuthorizeAsync(inNorth, $"/divisions/{north:D}/projects/documents", ActionType.Read));
        Assert.Equal(
            ProjectMediaAuthorizationModule.DivisionPermissionSource,
            await AuthorizeAsync(inNorth, $"/divisions/{north:D}/projects/documents", ActionType.Create));
        Assert.Equal(
            ProjectMediaAuthorizationModule.NoPermissionSource,
            await AuthorizeAsync(inSouth, $"/divisions/{south:D}/projects/documents", ActionType.Read));
        Assert.Equal(
            ProjectMediaAuthorizationModule.NoPermissionSource,
            await AuthorizeAsync(inSouth, $"/divisions/{south:D}/projects/documents", ActionType.Delete));
    }

    [Fact]
    public async Task MediaRoutesServeOnlySignedInUsersWithRightsInThePathsDivision()
    {
        var database = await postgreSql.CreateDatabaseAsync("media_routes");
        var storagePath = CreateTempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var north = SampleAuthorizationDefaults.NorthDivisionId;
        var south = SampleAuthorizationDefaults.SouthDivisionId;
        var northDocuments = $"/divisions/{north:D}/projects/{SampleAuthorizationDefaults.AlphaProjectId:D}/documents";
        var southDocuments = $"/divisions/{south:D}/projects/{SampleAuthorizationDefaults.BetaProjectId:D}/documents";
        try
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Services.AddHttpContextAccessor();
            builder.Services
                .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
                .AddScheme<AuthenticationSchemeOptions, MediaTestBearerHandler>(
                    JwtBearerDefaults.AuthenticationScheme,
                    _ => { });
            builder.Services.AddAuthorization(options => options.AddPolicy(
                "app.project.view",
                policy => policy.RequireClaim(NhPlatformClaimTypes.Permission, "app.project.view")));
            builder.Services.AddControllers().AddApplicationPart(typeof(MediaSamplesController).Assembly);
            builder.Services.AddSingleton(new SampleDivisionPermissionStore()
                .Grant(MediaTestBearerHandler.NorthMemberId, north, "project.view", "project.manage")
                .UserManager);
            builder.Services.AddSingleton<SampleMediaEventLog>();
            builder.Services.AddSingleton<SampleMediaThumbnailStore>();
            builder.Services.AddSingleton<SampleMediaAuthorizationLog>();
            builder.Services.AddNhMedia(media =>
            {
                media.UsePostgreSqlFileStructureStorage(database, options =>
                {
                    options.Scheme = "project_media";
                    options.RunMigrations = false;
                });
                media.UseFileSystemMediaStorage(storagePath, true);
                media.AddAuthentication<ProjectMediaAuthorizationModule>();
                media.AddThumbnailService<ProjectMediaThumbnailService>();
                media.AddEventHandler<ProjectMediaEventHandler>();
            });
            builder.Services.AddScoped<ProjectMediaSampleService>();

            await using var app = builder.Build();
            await using (var scope = app.Services.CreateAsyncScope())
            {
                await scope.ServiceProvider.GetRequiredService<FileStructureDbContext>().Database
                    .MigrateAsync(cancellationToken);
            }

            app.UseAuthentication();
            app.UseAuthorization();
            app.MapControllers();
            app.MapSampleProjectMedia();
            await app.StartAsync(cancellationToken);
            using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };

            // Anonymous requests are refused on every route, whatever permissions a header claims.
            Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(client, null, north, HttpMethod.Get,
                $"/project-media/list?path={Uri.EscapeDataString(northDocuments)}")).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(client, null, north, HttpMethod.Get,
                DownloadUrl("/project-media/download", northDocuments, "budget.txt"))).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(client, null, north, HttpMethod.Post,
                "/project-media/upload", UploadContent(northDocuments, "budget.txt", "planted"))).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(client, null, north, HttpMethod.Post,
                $"/project-media/folder?path={Uri.EscapeDataString(northDocuments)}&folderName=planted")).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(client, null, north, HttpMethod.Delete,
                DownloadUrl("/project-media/file", northDocuments, "budget.txt"))).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(client, null, north, HttpMethod.Get,
                DownloadUrl("/media-samples/download", northDocuments, "budget.txt"))).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(client, null, north, HttpMethod.Get,
                "/media-samples/diagnostics")).StatusCode);

            // The application permissions of the manager cover the projects of every division.
            Assert.Equal(HttpStatusCode.OK, (await SendAsync(client, "manager", north, HttpMethod.Post,
                "/project-media/upload", UploadContent(northDocuments, "budget.txt", "north budget"))).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await SendAsync(client, "manager", south, HttpMethod.Post,
                "/project-media/upload", UploadContent(southDocuments, "plan.txt", "south plan"))).StatusCode);
            var managerList = await SendAsync(client, "manager", north, HttpMethod.Get,
                $"/project-media/list?path={Uri.EscapeDataString(northDocuments)}");
            Assert.Equal(HttpStatusCode.OK, managerList.StatusCode);
            Assert.Contains("budget.txt", await managerList.Content.ReadAsStringAsync(cancellationToken));

            // The North member's division permissions come from the user store and cover North only.
            var memberDownload = await SendAsync(client, "north-member", north, HttpMethod.Get,
                DownloadUrl("/media-samples/download", northDocuments, "budget.txt"));
            Assert.Equal(HttpStatusCode.OK, memberDownload.StatusCode);
            Assert.Equal("north budget", await memberDownload.Content.ReadAsStringAsync(cancellationToken));
            Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(client, "north-member", south, HttpMethod.Get,
                DownloadUrl("/project-media/download", southDocuments, "plan.txt"))).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(client, "north-member", north, HttpMethod.Get,
                DownloadUrl("/project-media/download", southDocuments, "plan.txt"))).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(client, "north-member", south, HttpMethod.Delete,
                DownloadUrl("/project-media/file", southDocuments, "plan.txt"))).StatusCode);

            // A signed-in user without project rights is refused, and only application-wide
            // project viewers see the diagnostics, whose decisions cover every division.
            Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(client, "outsider", north, HttpMethod.Get,
                DownloadUrl("/project-media/download", northDocuments, "budget.txt"))).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(client, "outsider", north, HttpMethod.Delete,
                DownloadUrl("/project-media/file", northDocuments, "budget.txt"))).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(client, "outsider", north, HttpMethod.Get,
                "/media-samples/diagnostics")).StatusCode);
            var diagnostics = await SendAsync(client, "manager", north, HttpMethod.Get, "/media-samples/diagnostics");
            Assert.Equal(HttpStatusCode.OK, diagnostics.StatusCode);
            var decisions = await diagnostics.Content.ReadAsStringAsync(cancellationToken);
            Assert.Contains(ProjectMediaAuthorizationModule.ApplicationPermissionSource, decisions);
            Assert.Contains(ProjectMediaAuthorizationModule.DivisionPermissionSource, decisions);

            Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(client, "north-member", north, HttpMethod.Delete,
                DownloadUrl("/project-media/file", northDocuments, "budget.txt"))).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(client, "manager", north, HttpMethod.Get,
                DownloadUrl("/project-media/download", northDocuments, "budget.txt"))).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await SendAsync(client, "manager", south, HttpMethod.Get,
                DownloadUrl("/project-media/download", southDocuments, "plan.txt"))).StatusCode);

            await app.StopAsync(cancellationToken);
        }
        finally
        {
            Directory.Delete(storagePath, recursive: true);
        }
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

    /// <summary>
    /// Runs the module for one operation and returns the source of the decision it logged.
    /// </summary>
    private static async Task<string> AuthorizeAsync(HttpContext request, string path, ActionType action)
    {
        var log = new SampleMediaAuthorizationLog();
        var module = new ProjectMediaAuthorizationModule(new HttpContextAccessor { HttpContext = request }, log);
        var context = CreateAuthorizationContext(path, action);

        await module.IsAuthorizedAsync(context);

        var decision = Assert.Single(log.Items);
        Assert.Equal(context.Authorized, decision.Authorized);
        Assert.Equal(
            context.Authorized,
            decision.Source != ProjectMediaAuthorizationModule.NoPermissionSource);
        return decision.Source;
    }

    private static DefaultHttpContext CreateMediaRequest(
        Guid activeDivisionId,
        ClaimsPrincipal user,
        IServiceProvider? requestServices = null)
    {
        var httpContext = new DefaultHttpContext { User = user };
        if (requestServices is not null)
        {
            httpContext.RequestServices = requestServices;
        }

        httpContext.Request.Headers[NewHeap.Platform.AspNet.Common.Constants.HttpHeaderKeys.ActiveDivisionId] =
            activeDivisionId.ToString();
        return httpContext;
    }

    private static ClaimsPrincipal SignedIn(params string[] applicationPermissions)
    {
        return SignedIn(
            Guid.NewGuid(),
            applicationPermissions
                .Select(permission => new Claim(NhPlatformClaimTypes.Permission, permission))
                .ToArray());
    }

    private static ClaimsPrincipal SignedIn(Guid userId, params Claim[] claims)
    {
        return new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, userId.ToString()), .. claims],
            "sample-test"));
    }

    private static IServiceProvider UserStore(SampleDivisionPermissionStore? store = null)
    {
        return new ServiceCollection()
            .AddSingleton((store ?? new SampleDivisionPermissionStore()).UserManager)
            .BuildServiceProvider();
    }

    /// <summary>
    /// Sends a media request in <paramref name="activeDivisionId"/> as <paramref name="user"/>, or
    /// anonymously. Every request also claims both project permissions in a header, which the API
    /// must ignore.
    /// </summary>
    private static async Task<HttpResponseMessage> SendAsync(
        HttpClient client,
        string? user,
        Guid activeDivisionId,
        HttpMethod method,
        string url,
        HttpContent? content = null)
    {
        using var request = new HttpRequestMessage(method, url) { Content = content };
        request.Headers.Add(
            NewHeap.Platform.AspNet.Common.Constants.HttpHeaderKeys.ActiveDivisionId,
            activeDivisionId.ToString());
        request.Headers.Add(ClientPermissionsHeader, "app.project.view,app.project.manage");
        if (user is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", user);
        }

        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static string DownloadUrl(string route, string path, string fileName)
    {
        return $"{route}?path={Uri.EscapeDataString(path)}&fileName={Uri.EscapeDataString(fileName)}";
    }

    private static MultipartFormDataContent UploadContent(string path, string fileName, string text)
    {
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes(text));
        file.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        return new MultipartFormDataContent
        {
            { file, "file", fileName },
            { new StringContent(fileName), "fileName" },
            { new StringContent(path), "path" }
        };
    }

    /// <summary>
    /// Signs in the route test's users under the bearer scheme name that the media routes require.
    /// <c>Authorization: Bearer manager</c> holds the application-wide project permissions,
    /// <c>north-member</c> only the division permissions the user store grants, and
    /// <c>outsider</c> no project rights. Without the header the request stays anonymous.
    /// </summary>
    private sealed class MediaTestBearerHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public static readonly Guid ManagerId = Guid.Parse("4f1b8c6e-0d2a-4a51-9a39-2d7e5f0c1a01");
        public static readonly Guid NorthMemberId = Guid.Parse("4f1b8c6e-0d2a-4a51-9a39-2d7e5f0c1a02");
        public static readonly Guid OutsiderId = Guid.Parse("4f1b8c6e-0d2a-4a51-9a39-2d7e5f0c1a03");

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var principal = Request.Headers.Authorization.ToString() switch
            {
                "Bearer manager" => SignedIn(
                    ManagerId,
                    new Claim(NhPlatformClaimTypes.Permission, "app.project.view"),
                    new Claim(NhPlatformClaimTypes.Permission, "app.project.manage")),
                "Bearer north-member" => SignedIn(NorthMemberId),
                "Bearer outsider" => SignedIn(OutsiderId),
                _ => null
            };

            if (principal is null)
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name)));
        }
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

    private static async Task<FileReference> CreateFileAsync(IMediaLibraryService media, string path, string name)
    {
        await using var content = new MemoryStream(Encoding.UTF8.GetBytes(name));
        var created = await media.CreateFileAsync(new FileModel { Path = path, Name = name }, content);
        Assert.True(created.Success);
        return created.Data!;
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "SampleProjectManagement.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
