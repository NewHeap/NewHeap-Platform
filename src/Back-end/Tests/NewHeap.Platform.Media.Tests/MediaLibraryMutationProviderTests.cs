using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NewHeap.Media;
using NewHeap.Media.FileStructureStorage.SqlServer;
using NewHeap.Media.Modules;
using NewHeap.Platform.Tests.Relational;
using NSubstitute;
using Xunit;

namespace NewHeap.Platform.Media.Tests;

/// <summary>
/// <see cref="MediaLibraryService"/> mutations over the real relational file-structure storage of each
/// provider: the existing-name checks of moves and renames follow the provider's own path and name lookup.
/// </summary>
public sealed class MediaLibraryMutationProviderTests
{
    [Theory]
    [InlineData(RelationalTestProvider.SqlServer)]
    [InlineData(RelationalTestProvider.PostgreSql)]
    public async Task MovesAndRenamesRefuseANameTheDestinationAlreadyHolds(RelationalTestProvider provider)
    {
        await using var media = await MediaLibrary.CreateAsync(provider);
        var existing = await media.CreateFileAsync("/projects/docs", "report.pdf");
        var draft = await media.CreateFileAsync("/projects/drafts", "report.pdf");
        await media.Storage.CreateFolderAsync("/projects", "staging");
        await media.Storage.CreateFolderAsync("/projects", "archive");

        var fileMove = await media.Service.UpdateFileAsync(draft, new FileModel
        {
            Path = "/projects/docs",
            Name = "report.pdf"
        });
        var folderMove = await media.Service.UpdateFolderAsync("/projects", "staging", "/projects", "archive");

        Assert.False(fileMove.Success);
        Assert.Equal(NhMediaErrorCodes.FileNameExists, Assert.Single(fileMove.GetResultItems())
            .ErrorMessages.Single().ToString());
        Assert.False(folderMove.Success);
        Assert.Equal(NhMediaErrorCodes.FolderNameExists, Assert.Single(folderMove.GetResultItems())
            .ErrorMessages.Single().ToString());
        Assert.Equal(existing, (await media.Storage.GetFileAsync("/projects/docs", "report.pdf", null))?.Id);
        Assert.Equal(draft, (await media.Storage.GetFileAsync("/projects/drafts", "report.pdf", null))?.Id);
        Assert.NotNull((await media.Storage.GetFolderReferenceAsync("/projects/staging")).Id);

        // A free name in the destination moves the file.
        Assert.True((await media.Service.UpdateFileAsync(draft, new FileModel
        {
            Path = "/projects/docs",
            Name = "report-draft.pdf"
        })).Success);
        Assert.Equal(draft, (await media.Storage.GetFileAsync("/projects/docs", "report-draft.pdf", null))?.Id);
    }

    [Theory]
    [InlineData(RelationalTestProvider.SqlServer)]
    [InlineData(RelationalTestProvider.PostgreSql)]
    public async Task ANameThatDiffersOnlyInCaseConflictsWhereTheProviderLooksNamesUpCaseInsensitively(
        RelationalTestProvider provider)
    {
        await using var media = await MediaLibrary.CreateAsync(provider);
        var existing = await media.CreateFileAsync("/projects/docs", "report.pdf");
        var draft = await media.CreateFileAsync("/projects/drafts", "report.pdf");

        var move = await media.Service.UpdateFileAsync(draft, new FileModel
        {
            Path = "/projects/docs",
            Name = "Report.pdf"
        });

        // SQL Server compares paths and names case-insensitively, so a path read of either name would
        // resolve to an undefined one of the two files; PostgreSQL keeps them apart.
        Assert.Equal(provider == RelationalTestProvider.PostgreSql, move.Success);
        Assert.Equal(existing, (await media.Storage.GetFileAsync("/projects/docs", "report.pdf", null))?.Id);

        // Changing only the case of a file's own name is never a conflict with itself.
        Assert.True((await media.Service.UpdateFileAsync(existing, new FileModel
        {
            Path = "/projects/docs",
            Name = "REPORT.pdf"
        })).Success);
    }

    private sealed class MediaLibrary : IAsyncDisposable
    {
        private readonly ServiceProvider _provider;
        private readonly AsyncServiceScope _scope;

        private MediaLibrary(ServiceProvider provider)
        {
            _provider = provider;
            _scope = provider.CreateAsyncScope();
            Storage = _scope.ServiceProvider.GetRequiredService<IFileStructureStorage>();
            Service = new MediaLibraryService([], new ThumbnailService(), Storage, Substitute.For<IMediaStorage>(),
                new DefaultAuthorizationModule(), NullLogger<MediaLibraryService>.Instance);
        }

        public IFileStructureStorage Storage { get; }

        public MediaLibraryService Service { get; }

        public static async Task<MediaLibrary> CreateAsync(RelationalTestProvider provider)
        {
            var database = await RelationalTestServer.For(provider).CreateDatabaseAsync("media_mutation");
            var services = new ServiceCollection();
            services.AddLogging();
            if (provider == RelationalTestProvider.SqlServer)
            {
                services.AddMediaSqlServerStorage(database, options => options.RunMigrations = false);
            }
            else
            {
                services.AddMediaPostgreSqlStorage(database, options => options.RunMigrations = false);
            }

            var media = new MediaLibrary(services.BuildServiceProvider());
            await media._scope.ServiceProvider.GetRequiredService<FileStructureDbContext>().Database.MigrateAsync();
            return media;
        }

        public async Task<Guid> CreateFileAsync(string path, string name)
        {
            var id = Guid.NewGuid();
            Assert.True((await Storage.CreateFileAsync(new FileModel { Path = path, Name = name }, id)).Success);
            return id;
        }

        public async ValueTask DisposeAsync()
        {
            await _scope.DisposeAsync();
            await _provider.DisposeAsync();
        }
    }
}
