using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NewHeap.Media;
using NewHeap.Media.FileStructureStorage.SqlServer;
using NewHeap.Media.Modules;
using NewHeap.Platform.AspNet.Common;
using NewHeap.Platform.Common.Identity.Claims;
using SampleProjectManagement.Api.Events;
using SampleProjectManagement.Api.Services;
using Xunit;

namespace SampleProjectManagement.Core.Tests;

/// <summary>
/// SPM-181 and SPM-184: the sample's division-scoped <see cref="ProjectMediaAuthorizationModule"/> decides every
/// media mutation where it writes. A division manager cannot move, rename or retag media into or in another
/// division, a viewer cannot localize, and only a file's display strings can be localized.
/// </summary>
public sealed class MediaMutationAuthorizationSamplesTests(SamplePostgreSqlServer postgreSql)
{
    private static readonly Guid DivisionA = Guid.Parse("aaaaaaaa-0000-0000-0000-00000000000a");
    private static readonly Guid DivisionB = Guid.Parse("bbbbbbbb-0000-0000-0000-00000000000b");

    private static readonly string ProjectsA = $"/divisions/{DivisionA:D}/projects";
    private static readonly string ProjectsB = $"/divisions/{DivisionB:D}/projects";

    [Fact]
    public async Task MovesRenamesAndTagUpdatesStayInsideTheActiveDivision()
    {
        await using var sample = await MediaSample.CreateAsync(postgreSql);

        sample.ActAs(DivisionA, "project.view", "project.manage");
        var report = await sample.UploadAsync($"{ProjectsA}/docs", "report.pdf");
        var draft = await sample.UploadAsync($"{ProjectsA}/drafts", "report.pdf");
        Assert.True((await sample.Media.CreateFolderAsync(ProjectsA, "staging")).Success);
        sample.ActAs(DivisionB, "project.view", "project.manage");
        var victim = await sample.UploadAsync($"{ProjectsB}/docs", "report.pdf");

        // Division A's manager may change its own media, but cannot place it in division B's folders.
        sample.ActAs(DivisionA, "project.view", "project.manage");
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => sample.Media.UpdateFileAsync(report.Id,
            new FileModel { Path = $"{ProjectsB}/docs", Name = "report.pdf" }));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            sample.Media.UpdateFolderAsync(ProjectsA, "staging", ProjectsB, "planted"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            sample.Media.UpdateFileTagsAsync($"{ProjectsB}/docs", "report.pdf", ["planted"]));
        Assert.Contains(sample.AuthorizationLog.Items, decision =>
            decision.Action == ActionType.Create && decision.Path == $"{ProjectsB}/docs" && !decision.Authorized);

        // Inside its own division a move onto an existing name is refused, and a free name is accepted.
        var duplicate = await sample.Media.UpdateFileAsync(draft.Id,
            new FileModel { Path = $"{ProjectsA}/docs", Name = "report.pdf" });
        Assert.False(duplicate.Success);
        Assert.Contains(duplicate.GetResultItems(), item =>
            item.ErrorMessages.Any(message => message.ToString() == NhMediaErrorCodes.FileNameExists));
        Assert.True((await sample.Media.UpdateFileAsync(draft.Id,
            new FileModel { Path = $"{ProjectsA}/docs", Name = "report-draft.pdf" })).Success);
        Assert.True((await sample.Media.UpdateFileTagsAsync($"{ProjectsA}/docs", "report.pdf", ["final"])).Success);

        // Division B still sees only its own file, with its own tags.
        sample.ActAs(DivisionB, "project.view");
        var docsB = await sample.Media.GetFolder($"{ProjectsB}/docs");
        var listed = Assert.Single(docsB.Files);
        Assert.Equal(victim.Id, listed.Id);
        Assert.Empty(listed.Tags);
        Assert.DoesNotContain((await sample.Media.GetFolder(ProjectsB)).Folders, folder => folder.Name == "planted");
    }

    [Fact]
    public async Task ViewersCannotLocalizeAndOnlyDisplayStringsAreLocalized()
    {
        await using var sample = await MediaSample.CreateAsync(postgreSql);
        sample.ActAs(DivisionA, "project.view", "project.manage");
        var plan = await sample.UploadAsync($"{ProjectsA}/docs", "plan.txt");

        sample.ActAs(DivisionA, "project.view");
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            sample.Media.LocalizeFieldAsync(plan.Id, "title", "nl", "Defaced by a viewer"));

        sample.ActAs(DivisionA, "project.view", "project.manage");
        var name = await sample.Media.LocalizeFieldAsync(plan.Id, "name", "nl", "approved-invoice.pdf");
        Assert.False(name.Success);
        Assert.Contains(name.GetResultItems(), item =>
            item.ErrorMessages.Any(message => message.ToString() == NhMediaErrorCodes.PropertyNotLocalizable));
        Assert.True((await sample.Media.LocalizeFieldAsync(plan.Id, "title", "nl", "Projectplan")).Success);

        sample.ActAs(DivisionA, "project.view");
        var localized = Assert.Single((await sample.Media.GetFolder($"{ProjectsA}/docs", "nl")).Files);
        Assert.Equal("plan.txt", localized.Name);
        Assert.Equal("Projectplan", localized.Title);
    }

    /// <summary>
    /// The sample's media composition on a PostgreSQL database of its own, with a request whose active
    /// division and division permissions a test chooses.
    /// </summary>
    private sealed class MediaSample : IAsyncDisposable
    {
        private readonly ServiceProvider _provider;
        private readonly AsyncServiceScope _scope;
        private readonly string _storagePath;

        private MediaSample(ServiceProvider provider, string storagePath)
        {
            _provider = provider;
            _storagePath = storagePath;
            _scope = provider.CreateAsyncScope();
            Media = _scope.ServiceProvider.GetRequiredService<IMediaLibraryService>();
            AuthorizationLog = _scope.ServiceProvider.GetRequiredService<SampleMediaAuthorizationLog>();
        }

        public IMediaLibraryService Media { get; }

        public SampleMediaAuthorizationLog AuthorizationLog { get; }

        public static async Task<MediaSample> CreateAsync(SamplePostgreSqlServer postgreSql)
        {
            var database = await postgreSql.CreateDatabaseAsync("media_mutation");
            var storagePath = Path.Combine(Path.GetTempPath(), "SampleProjectManagement.Tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(storagePath);

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

            var sample = new MediaSample(services.BuildServiceProvider(), storagePath);
            await sample._scope.ServiceProvider.GetRequiredService<FileStructureDbContext>().Database
                .MigrateAsync(TestContext.Current.CancellationToken);
            return sample;
        }

        /// <summary>
        /// Makes the following calls a request in <paramref name="divisionId"/> by a member with the given
        /// division permissions, such as <c>project.view</c> and <c>project.manage</c>.
        /// </summary>
        public void ActAs(Guid divisionId, params string[] divisionPermissions)
        {
            var claims = divisionPermissions.Select(permission =>
                new Claim(NhPlatformClaimTypes.DivisionPermission, $"{divisionId:D}_{permission}"));
            var httpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(claims, "sample"))
            };
            httpContext.Request.Headers[Constants.HttpHeaderKeys.ActiveDivisionId] = divisionId.ToString();
            _scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>().HttpContext = httpContext;
        }

        public async Task<FileReference> UploadAsync(string path, string name)
        {
            await using var content = new MemoryStream(Encoding.UTF8.GetBytes(name));
            var created = await Media.CreateFileAsync(new FileModel { Path = path, Name = name }, content);
            Assert.True(created.Success);
            return created.Data!;
        }

        public async ValueTask DisposeAsync()
        {
            await _scope.DisposeAsync();
            await _provider.DisposeAsync();
            Directory.Delete(_storagePath, recursive: true);
        }
    }
}
