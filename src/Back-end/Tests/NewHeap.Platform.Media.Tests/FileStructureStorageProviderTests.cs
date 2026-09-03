using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NewHeap.Media;
using NewHeap.Media.FileStructureStorage.SqlServer;
using NewHeap.Media.FileStructureStorage.SqlServer.Entities;
using NewHeap.Media.Models;
using NewHeap.Media.Modules;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Testcontainers.MsSql;
using Testcontainers.PostgreSql;
using Xunit;

namespace NewHeap.Platform.Media.Tests;

public sealed class FileStructureStorageProviderTests
{
    [Fact]
    public async Task ProvidersConfigureOnlyTheirOwnLookupColumns()
    {
        var sqlServerServices = new ServiceCollection();
        sqlServerServices.AddMediaSqlServerStorage("Server=localhost;Database=nh_media", options => options.RunMigrations = false);
        await using var sqlServerProvider = sqlServerServices.BuildServiceProvider();
        await using var sqlServerScope = sqlServerProvider.CreateAsyncScope();
        var sqlServerModel = sqlServerScope.ServiceProvider.GetRequiredService<FileStructureDbContext>().Model;

        Assert.NotNull(sqlServerModel.FindEntityType(typeof(FileEntity))!.FindProperty("PathLookup"));
        Assert.NotNull(sqlServerModel.FindEntityType(typeof(FileEntity))!.FindProperty("PathNameLookup"));
        Assert.NotNull(sqlServerModel.FindEntityType(typeof(FolderEntity))!.FindProperty("PathLookup"));
        Assert.NotNull(sqlServerModel.FindEntityType(typeof(FolderEntity))!.FindProperty("PathNameLookup"));

        var postgreSqlServices = new ServiceCollection();
        postgreSqlServices.AddMediaPostgreSqlStorage("Host=localhost;Database=nh_media", options => options.RunMigrations = false);
        await using var postgreSqlProvider = postgreSqlServices.BuildServiceProvider();
        await using var postgreSqlScope = postgreSqlProvider.CreateAsyncScope();
        var postgreSqlModel = postgreSqlScope.ServiceProvider.GetRequiredService<FileStructureDbContext>().Model;

        Assert.Null(postgreSqlModel.FindEntityType(typeof(FileEntity))!.FindProperty("PathLookup"));
        Assert.Null(postgreSqlModel.FindEntityType(typeof(FileEntity))!.FindProperty("PathNameLookup"));
        Assert.Null(postgreSqlModel.FindEntityType(typeof(FolderEntity))!.FindProperty("PathLookup"));
        Assert.Null(postgreSqlModel.FindEntityType(typeof(FolderEntity))!.FindProperty("PathNameLookup"));
        Assert.NotNull(postgreSqlModel.FindEntityType(typeof(FileEntity))!.FindProperty("PathLookupHash"));
        Assert.NotNull(postgreSqlModel.FindEntityType(typeof(FileEntity))!.FindProperty("PathNameLookupHash"));
        Assert.NotNull(postgreSqlModel.FindEntityType(typeof(FolderEntity))!.FindProperty("PathLookupHash"));
        Assert.NotNull(postgreSqlModel.FindEntityType(typeof(FolderEntity))!.FindProperty("PathNameLookupHash"));
    }

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
            postgreSql.GetConnectionString(), options => options.RunMigrations = false),
            verifyQueryPlan: AssertPostgreSqlUsesLookupIndexAsync,
            migrateDatabase: MigratePostgreSqlWithExistingLookupRowAsync);
    }

    private static async Task VerifyProviderAsync(Action<IServiceCollection> configureProvider,
        Func<FileStructureDbContext, Task>? verifyQueryPlan = null,
        Func<FileStructureDbContext, Task>? migrateDatabase = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        configureProvider(services);
        await using var serviceProvider = services.BuildServiceProvider();
        await using var scope = serviceProvider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<FileStructureDbContext>();
        if (migrateDatabase is null)
        {
            await dbContext.Database.MigrateAsync();
        }
        else
        {
            await migrateDatabase(dbContext);
        }

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

        if (verifyQueryPlan is not null)
        {
            await verifyQueryPlan(dbContext);
        }
    }

    private static async Task MigratePostgreSqlWithExistingLookupRowAsync(FileStructureDbContext dbContext)
    {
        const string migrationId = "20260903134647_IndexSeekLookup";
        const string path = "/Migrated-Lookup";
        const string name = "Existing.TXT";
        var id = Guid.NewGuid();

        await dbContext.Database.MigrateAsync(migrationId);
        await dbContext.Database.OpenConnectionAsync();
        try
        {
            await using var insertCommand = dbContext.Database.GetDbConnection().CreateCommand();
            insertCommand.CommandText = """
                INSERT INTO "nhmedia"."Files"
                    ("Id", "Name", "Path", "CreationDateTime", "Tags", "PathLookup", "PathNameLookup")
                VALUES
                    (@id, @name, @path, CURRENT_TIMESTAMP, ARRAY[]::text[], lower(@path), lower(@path) || chr(31) || lower(@name))
                """;
            AddParameter(insertCommand, "id", id);
            AddParameter(insertCommand, "name", name);
            AddParameter(insertCommand, "path", path);
            await insertCommand.ExecuteNonQueryAsync();
        }
        finally
        {
            await dbContext.Database.CloseConnectionAsync();
        }

        await dbContext.Database.MigrateAsync();
        await dbContext.Database.OpenConnectionAsync();
        try
        {
            await using var selectCommand = dbContext.Database.GetDbConnection().CreateCommand();
            selectCommand.CommandText = """
                SELECT "PathNameLookupHash"
                FROM "nhmedia"."Files"
                WHERE "Id" = @id
                """;
            AddParameter(selectCommand, "id", id);

            var lookupHash = Assert.IsType<byte[]>(await selectCommand.ExecuteScalarAsync());
            Assert.Equal(ComputePostgreSqlLookupHash(path, name), lookupHash);
        }
        finally
        {
            await dbContext.Database.CloseConnectionAsync();
        }
    }

    private static async Task AssertPostgreSqlUsesLookupIndexAsync(FileStructureDbContext dbContext)
    {
        const string targetPath = "/lookup-target";
        const string targetName = "target.txt";
        var files = Enumerable.Range(0, 2_048)
            .Select(index => new FileEntity
            {
                Id = Guid.NewGuid(),
                Path = $"/lookup-data/{index:D5}",
                Name = "file.txt"
            })
            .Append(new FileEntity
            {
                Id = Guid.NewGuid(),
                Path = targetPath,
                Name = targetName
            });

        dbContext.Files.AddRange(files);
        await dbContext.SaveChangesAsync();
        await dbContext.Database.ExecuteSqlRawAsync("ANALYZE \"nhmedia\".\"Files\"");

        await AssertPostgreSqlUsesIndexAsync(
            dbContext,
            """
            SELECT "Id"
            FROM "nhmedia"."Files"
            WHERE "PathNameLookupHash" = @pathNameLookupHash
              AND "Path" = @path
              AND "Name" = @name
            """,
            "IX_Files_PathNameLookupHash",
            ("pathNameLookupHash", ComputePostgreSqlLookupHash(targetPath, targetName)),
            ("path", targetPath),
            ("name", targetName));

        await AssertPostgreSqlUsesIndexAsync(
            dbContext,
            """
            SELECT "Id"
            FROM "nhmedia"."Files"
            WHERE "PathLookupHash" = @pathLookupHash
              AND "Path" = @path
            """,
            "IX_Files_PathLookupHash",
            ("pathLookupHash", ComputePostgreSqlLookupHash(targetPath)),
            ("path", targetPath));
    }

    private static async Task AssertPostgreSqlUsesIndexAsync(FileStructureDbContext dbContext, string query,
        string expectedIndexName, params (string Name, object Value)[] parameters)
    {
        await dbContext.Database.OpenConnectionAsync();
        try
        {
            await using var command = dbContext.Database.GetDbConnection().CreateCommand();
            command.CommandText = "EXPLAIN (ANALYZE, COSTS OFF, FORMAT JSON)" + Environment.NewLine + query;
            foreach (var (name, value) in parameters)
            {
                AddParameter(command, name, value);
            }

            var planJson = (string)(await command.ExecuteScalarAsync())!;
            using var plan = JsonDocument.Parse(planJson);
            var nodeTypes = GetPlanNodeTypes(plan.RootElement).ToArray();

            Assert.Contains("Index Scan", nodeTypes);
            Assert.DoesNotContain("Seq Scan", nodeTypes);
            Assert.Contains(expectedIndexName, planJson, StringComparison.Ordinal);
        }
        finally
        {
            await dbContext.Database.CloseConnectionAsync();
        }
    }

    private static void AddParameter(System.Data.Common.DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private static byte[] ComputePostgreSqlLookupHash(params string?[] values)
    {
        var normalized = string.Join("\u001F", values.Select(value => value ?? string.Empty));
        return MD5.HashData(Encoding.UTF8.GetBytes(normalized));
    }

    private static IEnumerable<string> GetPlanNodeTypes(JsonElement node)
    {
        if (node.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in node.EnumerateArray())
            {
                foreach (var nodeType in GetPlanNodeTypes(item))
                {
                    yield return nodeType;
                }
            }

            yield break;
        }

        if (node.ValueKind != JsonValueKind.Object)
        {
            yield break;
        }

        if (node.TryGetProperty("Node Type", out var nodeTypeProperty))
        {
            yield return nodeTypeProperty.GetString()!;
        }

        if (node.TryGetProperty("Plan", out var plan))
        {
            foreach (var childNodeType in GetPlanNodeTypes(plan))
            {
                yield return childNodeType;
            }
        }

        if (node.TryGetProperty("Plans", out var plans))
        {
            foreach (var childNodeType in GetPlanNodeTypes(plans))
            {
                yield return childNodeType;
            }
        }
    }
}
