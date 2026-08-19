using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NewHeap.Media;
using NewHeap.Media.FileStructureStorage.SqlServer;
using NewHeap.Media.Models;
using NewHeap.Media.Modules;
using Testcontainers.MsSql;
using Testcontainers.PostgreSql;
using Xunit;

namespace NewHeap.Platform.Media.Tests;

public sealed class FileStructureStorageProviderTests
{
    [Fact]
    public async Task FileStructureStorageWorksOnBothRelationalProviders()
    {
        await using var sqlServer = new MsSqlBuilder(
            "mcr.microsoft.com/mssql/server:2022-CU14-ubuntu-22.04").Build();
        await sqlServer.StartAsync();
        await VerifyProviderAsync(services => services.AddMediaSqlServerStorage(
            sqlServer.GetConnectionString(), options => options.RunMigrations = false));

        await using var postgreSql = new PostgreSqlBuilder("postgres:15.1").Build();
        await postgreSql.StartAsync();
        await VerifyProviderAsync(services => services.AddMediaPostgreSqlStorage(
            postgreSql.GetConnectionString(), options => options.RunMigrations = false));
    }

    private static async Task VerifyProviderAsync(Action<IServiceCollection> configureProvider)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        configureProvider(services);
        await using var serviceProvider = services.BuildServiceProvider();
        await using var scope = serviceProvider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<FileStructureDbContext>();
        await dbContext.Database.MigrateAsync();

        var storage = scope.ServiceProvider.GetRequiredService<IFileStructureStorage>();
        var folder = await storage.CreateFolderAsync("/", "documents");
        Assert.Equal("/documents", folder.FullPath);

        var fileId = Guid.NewGuid();
        var created = await storage.CreateFileAsync(new FileModel
        {
            Path = folder.FullPath,
            Name = "release-notes.txt",
            Title = "Release notes",
            Tags = ["release", "public"]
        }, fileId);
        Assert.True(created.Success);
        Assert.Equal(fileId, created.Data?.Id);

        var fetched = await storage.GetFileAsync(folder.FullPath, "release-notes.txt", null);
        Assert.NotNull(fetched);
        Assert.Equal(fileId, fetched.Id);

        var search = await storage.SearchAsync("release", folder.FullPath, new SearchOptions());
        Assert.Contains(search.Results, result => result.Id == fileId);
    }
}
