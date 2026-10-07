using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using NewHeap.Media;
using NewHeap.Media.FileStructureStorage.SqlServer;
using NewHeap.Media.FileStructureStorage.SqlServer.Entities;
using NewHeap.Media.Models;
using NewHeap.Media.Modules;
using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Testcontainers.MsSql;
using Testcontainers.PostgreSql;
using Xunit;

namespace NewHeap.Platform.Media.Tests;

public sealed class FileStructureStorageProviderTests
{
    [Theory]
    [InlineData("project_media")]
    [InlineData("media\"archive")]
    public async Task PostgreSqlMigrationScriptsUseTheConfiguredSchema(string schema)
    {
        var services = new ServiceCollection();
        services.AddMediaPostgreSqlStorage("Host=localhost;Database=nh_media", options =>
        {
            options.Scheme = schema;
            options.RunMigrations = false;
        });
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<FileStructureDbContext>();
        var migrator = context.GetService<IMigrator>();
        var script = migrator.GenerateScript();
        var quotedSchema = context.GetService<ISqlGenerationHelper>().DelimitIdentifier(schema);

        Assert.Equal(schema, context.Model.GetDefaultSchema());
        Assert.Contains($"CREATE TABLE {quotedSchema}.\"Files\"", script);
        Assert.Contains($"UPDATE {quotedSchema}.\"Files\"", script);
        Assert.DoesNotContain("\"nhmedia\".", script);
        Assert.DoesNotContain("nhmedia.", script);
        var rollback = migrator.GenerateScript("20260903141415_UseFixedLookupHashes", "0");
        Assert.Contains($"DROP TABLE {quotedSchema}.\"Files\"", rollback);
        Assert.DoesNotContain("nhmedia.", rollback);
        Assert.DoesNotContain("\"nhmedia\".", rollback);
    }

    [Theory]
    [InlineData("nhmedia")]
    [InlineData("project_media")]
    [InlineData("media\"archive")]
    public async Task PostgreSqlUpgradesOriginalSchemaAndPreservesFilesAndFolders(string schema)
    {
        await using var database = new PostgreSqlBuilder("postgres:15.1").Build();
        await database.StartAsync();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMediaPostgreSqlStorage(database.GetConnectionString(), options =>
        {
            options.Scheme = schema;
            options.RunMigrations = false;
        });
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<FileStructureDbContext>();
        await context.Database.MigrateAsync("20260805125944_InitialPostgreSql");
        var qualifiedSchema = context.GetService<ISqlGenerationHelper>().DelimitIdentifier(schema);
        var fileId = Guid.NewGuid();
        var folderId = Guid.NewGuid();
        var folderName = "Archive-" + new string('x', 300);
        var path = "/" + folderName;
        await context.Database.OpenConnectionAsync();
        try
        {
            await using var command = context.Database.GetDbConnection().CreateCommand();
            command.CommandText = $"""
                INSERT INTO {qualifiedSchema}."Folders" ("Id", "Path", "Name", "PathLookupHash", "PathNameLookupHash")
                VALUES (@folderId, '/', @folderName, decode(md5('/'), 'hex'), decode(md5(@path), 'hex'));
                INSERT INTO {qualifiedSchema}."Files"
                    ("Id", "Path", "Name", "CreationDateTime", "Tags", "PathLookupHash", "PathNameLookupHash")
                VALUES (@fileId, @path, 'Existing.TXT', CURRENT_TIMESTAMP, ARRAY['archive'],
                    decode(md5(@path), 'hex'), decode(md5(@path), 'hex'));
                """;
            AddParameter(command, "folderId", folderId);
            AddParameter(command, "fileId", fileId);
            AddParameter(command, "folderName", folderName);
            AddParameter(command, "path", path);
            await command.ExecuteNonQueryAsync();
        }
        finally
        {
            await context.Database.CloseConnectionAsync();
        }

        await context.Database.MigrateAsync();
        Assert.Empty(await context.Database.GetPendingMigrationsAsync());
        var storage = scope.ServiceProvider.GetRequiredService<IFileStructureStorage>();
        Assert.Equal(folderId, (await storage.GetFolderReferenceAsync(path)).Id);
        Assert.Equal(fileId, (await storage.GetFileAsync(path, "Existing.TXT", null))?.Id);
        Assert.Equal(fileId, Assert.Single((await storage.GetFolderAsync(path, null, null)).Files).Id);
        var uploadedId = Guid.NewGuid();
        Assert.True((await storage.CreateFileAsync(new FileModel { Path = path, Name = "new.txt" }, uploadedId)).Success);
        Assert.Equal(uploadedId, (await storage.GetFileAsync(path, "new.txt", null))?.Id);
        Assert.True((await storage.UpdateFileAsync(uploadedId, new FileModel { Path = path, Name = "renamed.txt" })).Success);
        Assert.Equal(uploadedId, (await storage.GetFileAsync(path, "renamed.txt", null))?.Id);
    }

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

    [Fact]
    public async Task FileLookupsUseFixedRoundTripsAndLookupIndexesOnBothRelationalProviders()
    {
        await using var sqlServer = new MsSqlBuilder(
            "mcr.microsoft.com/mssql/server:2022-CU14-ubuntu-22.04").Build();
        await sqlServer.StartAsync();
        await VerifyFileLookupsAsync(
            services => services.AddMediaSqlServerStorage(
                sqlServer.GetConnectionString(), options => options.RunMigrations = false),
            "UPDATE STATISTICS",
            "IX_Files_PathNameLookup",
            "IX_Folders_PathNameLookup",
            AssertSqlServerCommandUsesIndexesAsync);

        await using var postgreSql = new PostgreSqlBuilder("postgres:15.1").Build();
        await postgreSql.StartAsync();
        await VerifyFileLookupsAsync(
            services => services.AddMediaPostgreSqlStorage(
                postgreSql.GetConnectionString(), options => options.RunMigrations = false),
            "ANALYZE",
            "IX_Files_PathNameLookupHash",
            "IX_Folders_PathNameLookupHash",
            AssertPostgreSqlCommandUsesIndexesAsync);
    }

    private static async Task VerifyFileLookupsAsync(Action<IServiceCollection> configureProvider,
        string updateStatisticsCommand,
        string fileLookupIndex,
        string folderLookupIndex,
        Func<FileStructureDbContext, RecordedCommand, string[], Task> assertCommandUsesIndexes)
    {
        var commands = new RecordingCommandInterceptor();
        var services = new ServiceCollection();
        services.AddLogging();
        configureProvider(services);
        services.AddDbContextPool<FileStructureDbContext>(options => options
            .ConfigureWarnings(warnings => warnings.Throw(CoreEventId.RowLimitingOperationWithoutOrderByWarning))
            .AddInterceptors(commands));
        await using var serviceProvider = services.BuildServiceProvider();
        await using var scope = serviceProvider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<FileStructureDbContext>();
        await dbContext.Database.MigrateAsync();
        var storage = scope.ServiceProvider.GetRequiredService<IFileStructureStorage>();

        // Enough rows that both planners prefer the lookup indexes over scanning the tables.
        var folders = Enumerable.Range(0, 2_048)
            .Select(index => new FolderEntity { Id = Guid.NewGuid(), Path = "/gallery", Name = $"album-{index:D4}" })
            .ToArray();
        var files = folders
            .Select(folder => new FileEntity
            {
                Id = Guid.NewGuid(),
                Path = $"/gallery/{folder.Name}",
                Name = "cover.png",
                Title = folder.Name
            })
            .ToArray();
        var rootFile = new FileEntity { Id = Guid.NewGuid(), Path = "/", Name = "logo.svg" };
        dbContext.Folders.Add(new FolderEntity { Path = "/", Name = "gallery" });
        dbContext.Folders.AddRange(folders);
        dbContext.Files.AddRange(files);
        dbContext.Files.Add(rootFile);
        await dbContext.SaveChangesAsync();
        dbContext.ChangeTracker.Clear();

        var sqlGenerationHelper = dbContext.GetService<ISqlGenerationHelper>();
        foreach (var entityType in new[] { typeof(FileEntity), typeof(FolderEntity) })
        {
            var table = dbContext.Model.FindEntityType(entityType)!;
            await dbContext.Database.ExecuteSqlRawAsync(updateStatisticsCommand + " " +
                sqlGenerationHelper.DelimitIdentifier(table.GetTableName()!, table.GetSchema()));
        }

        // A page that references 30 files in 30 folders costs one file query and one folder query.
        var page = files.Take(29).Select(file => file.Id).Append(rootFile.Id).ToArray();
        var missingId = Guid.NewGuid();
        commands.Clear();
        var references = await storage.GetByIdsAsync([.. page, page[0], missingId]);

        Assert.Equal(2, commands.Commands.Count);
        var fileIdsCommand = commands.Commands[0];
        var folderIdsCommand = commands.Commands[1];
        Assert.Equal(page.Order(), references.Keys.Order());
        for (var index = 0; index < 29; index++)
        {
            var reference = references[files[index].Id];
            Assert.Equal("cover.png", reference.Name);
            Assert.Equal(folders[index].Name, reference.Title);
            Assert.Equal(folders[index].Id, reference.Folder.Id);
            Assert.Equal("/gallery", reference.Folder.Path);
            Assert.Equal(folders[index].Name, reference.Folder.Name);
            Assert.Equal(files[index].Path, reference.Folder.FullPath);
        }

        Assert.Null(references[rootFile.Id].Folder.Id);
        Assert.Equal("/", references[rootFile.Id].Folder.FullPath);
        foreach (var reference in references.Values)
        {
            var folder = await storage.GetFolderReferenceAsync(reference.Folder.FullPath);
            Assert.Equal(folder.Id, reference.Folder.Id);
            Assert.Equal(folder.Path, reference.Folder.Path);
            Assert.Equal(folder.Name, reference.Folder.Name);
        }

        await assertCommandUsesIndexes(dbContext, fileIdsCommand, ["PK_Files"]);
        await assertCommandUsesIndexes(dbContext, folderIdsCommand, [folderLookupIndex]);

        // Large sets are split into fixed-size batches: 500 ids per file query and 50 folders per folder query.
        commands.Clear();
        var allReferences = await storage.GetByIdsAsync(files.Select(file => file.Id).Append(rootFile.Id));

        Assert.Equal(5 + 41, commands.Commands.Count);
        Assert.Equal(files.Length + 1, allReferences.Count);
        for (var index = 0; index < files.Length; index++)
        {
            Assert.Equal(folders[index].Id, allReferences[files[index].Id].Folder.Id);
        }

        commands.Clear();
        Assert.Empty(await storage.GetByIdsAsync([]));
        Assert.Empty(await storage.GetByIdsAsync([missingId]));
        Assert.Single(commands.Commands);

        commands.Clear();
        var byId = await storage.GetByIdAsync(files[0].Id);
        Assert.Equal(2, commands.Commands.Count);
        Assert.Equal(folders[0].Id, byId?.Folder.Id);

        // A lookup by path reads the file and its folder id in one indexed round trip.
        commands.Clear();
        var byPath = await storage.GetFileAsync(files[1].Path, "cover.png", null);
        var fileByPathCommand = Assert.Single(commands.Commands);
        Assert.Equal(files[1].Id, byPath?.Id);
        Assert.Equal(folders[1].Id, byPath?.Folder.Id);
        Assert.Equal("/gallery", byPath?.Folder.Path);
        Assert.Equal(folders[1].Name, byPath?.Folder.Name);
        Assert.Equal(files[1].Path, byPath?.Folder.FullPath);
        await assertCommandUsesIndexes(dbContext, fileByPathCommand, [fileLookupIndex, folderLookupIndex]);

        commands.Clear();
        var rootByPath = await storage.GetFileAsync("/", "logo.svg", null);
        Assert.Single(commands.Commands);
        Assert.Equal(rootFile.Id, rootByPath?.Id);
        Assert.Null(rootByPath?.Folder.Id);
        Assert.Equal("/", rootByPath?.Folder.FullPath);

        commands.Clear();
        Assert.Null(await storage.GetFileAsync(files[1].Path, "missing.png", null));
        Assert.Single(commands.Commands);

        // Localizations remain one extra query, and only when a language is requested.
        Assert.True((await storage.LocalizeAsync(files[1].Id, "nl", nameof(FileReference.Title), "Cover (nl)"))
            .Success);
        commands.Clear();
        var localized = await storage.GetFileAsync(files[1].Path, "cover.png", "nl");
        Assert.Equal(2, commands.Commands.Count);
        Assert.Equal("Cover (nl)", localized?.Title);

        // Search results from many folders resolve those folders together.
        commands.Clear();
        var search = await storage.SearchAsync("", "/gallery",
            new SearchOptions { PageSize = 30, IncludeTotalCount = false });
        Assert.Equal(2, commands.Commands.Count);
        Assert.Equal(30, search.Results.Count());
        foreach (var result in search.Results)
        {
            Assert.Equal(folders.Single(folder => folder.Name == result.Folder.Name).Id, result.Folder.Id);
        }
    }

    private static async Task AssertSqlServerCommandUsesIndexesAsync(FileStructureDbContext dbContext,
        RecordedCommand recorded, string[] expectedIndexNames)
    {
        var plans = new List<string>();
        await dbContext.Database.OpenConnectionAsync();
        try
        {
            var connection = dbContext.Database.GetDbConnection();
            await ExecuteNonQueryAsync(connection, "SET STATISTICS XML ON");
            await using (var command = CreateCommand(connection, recorded, recorded.CommandText))
            await using (var reader = await command.ExecuteReaderAsync())
            {
                do
                {
                    var isPlan = reader.FieldCount == 1 &&
                                 reader.GetName(0) == "Microsoft SQL Server 2005 XML Showplan";
                    while (await reader.ReadAsync())
                    {
                        if (isPlan)
                        {
                            plans.Add(reader.GetString(0));
                        }
                    }
                } while (await reader.NextResultAsync());
            }

            await ExecuteNonQueryAsync(connection, "SET STATISTICS XML OFF");
        }
        finally
        {
            await dbContext.Database.CloseConnectionAsync();
        }

        var plan = Assert.Single(plans);
        Assert.DoesNotContain("PhysicalOp=\"Table Scan\"", plan, StringComparison.Ordinal);
        Assert.DoesNotContain("PhysicalOp=\"Clustered Index Scan\"", plan, StringComparison.Ordinal);
        Assert.DoesNotContain("PhysicalOp=\"Index Scan\"", plan, StringComparison.Ordinal);
        foreach (var indexName in expectedIndexNames)
        {
            Assert.Contains($"Index=\"[{indexName}]\"", plan, StringComparison.Ordinal);
        }
    }

    private static async Task AssertPostgreSqlCommandUsesIndexesAsync(FileStructureDbContext dbContext,
        RecordedCommand recorded, string[] expectedIndexNames)
    {
        string planJson;
        await dbContext.Database.OpenConnectionAsync();
        try
        {
            await using var command = CreateCommand(dbContext.Database.GetDbConnection(), recorded,
                "EXPLAIN (ANALYZE, COSTS OFF, FORMAT JSON)" + Environment.NewLine + recorded.CommandText);
            planJson = (string)(await command.ExecuteScalarAsync())!;
        }
        finally
        {
            await dbContext.Database.CloseConnectionAsync();
        }

        using var plan = JsonDocument.Parse(planJson);
        Assert.DoesNotContain("Seq Scan", GetPlanNodeTypes(plan.RootElement));
        foreach (var indexName in expectedIndexNames)
        {
            Assert.Contains($"\"{indexName}\"", planJson, StringComparison.Ordinal);
        }
    }

    private static DbCommand CreateCommand(DbConnection connection, RecordedCommand recorded, string commandText)
    {
        var command = connection.CreateCommand();
        command.CommandText = commandText;
        foreach (var parameter in recorded.Parameters)
        {
            command.Parameters.Add(((ICloneable)parameter).Clone());
        }

        return command;
    }

    private static async Task ExecuteNonQueryAsync(DbConnection connection, string commandText)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task VerifyProviderAsync(Action<IServiceCollection> configureProvider,
        Func<FileStructureDbContext, Task>? verifyQueryPlan = null,
        Func<FileStructureDbContext, Task>? migrateDatabase = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        configureProvider(services);
        services.AddDbContextPool<FileStructureDbContext>(options =>
            options.ConfigureWarnings(warnings =>
                warnings.Throw(CoreEventId.RowLimitingOperationWithoutOrderByWarning)));
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

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            dbContext.Files.Take(1).ToListAsync());

        var storage = scope.ServiceProvider.GetRequiredService<IFileStructureStorage>();
        var folder = await storage.CreateFolderAsync("/", "documents");
        Assert.Equal("/documents", folder.FullPath);
        Assert.NotNull(folder.Id);

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

        var stableFolder = await storage.CreateFolderAsync("/", "stable-order");
        var lowerFileId = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var higherFileId = Guid.Parse("00000000-0000-0000-0000-000000000002");
        Assert.True((await storage.CreateFileAsync(new FileModel
        {
            Path = stableFolder.FullPath,
            Name = "stable-higher.txt",
            Title = "Stable order"
        }, higherFileId)).Success);
        Assert.True((await storage.CreateFileAsync(new FileModel
        {
            Path = stableFolder.FullPath,
            Name = "stable-lower.txt",
            Title = "Stable order"
        }, lowerFileId)).Success);

        var defaultPage = await storage.GetFolderAsync(
            stableFolder.FullPath,
            null,
            new FileGetOptions { PageSize = 1 });
        Assert.Equal(lowerFileId, Assert.Single(defaultPage.Files).Id);

        var sortedPage = await storage.GetFolderAsync(
            stableFolder.FullPath,
            null,
            new FileGetOptions
            {
                PageSize = 1,
                OrderBy =
                [
                    new SortOption
                    {
                        Key = nameof(FileEntity.Title),
                        Direction = Direction.Ascending
                    }
                ]
            });
        Assert.Equal(lowerFileId, Assert.Single(sortedPage.Files).Id);

        var stableSearch = await storage.SearchAsync(
            "stable",
            stableFolder.FullPath,
            new SearchOptions { PageSize = 10 });
        Assert.Equal(
            [lowerFileId, higherFileId],
            stableSearch.Results.Select(result => result.Id));

        var lowerFolderId = Guid.Parse("00000000-0000-0000-0000-000000000011");
        var higherFolderId = Guid.Parse("00000000-0000-0000-0000-000000000012");
        dbContext.Folders.AddRange(
            new FolderEntity
            {
                Id = higherFolderId,
                Path = "/",
                Name = "ordered-folder-higher"
            },
            new FolderEntity
            {
                Id = lowerFolderId,
                Path = "/",
                Name = "ordered-folder-lower"
            });
        await dbContext.SaveChangesAsync();

        var rootFolders = await storage.GetFolderAsync("/", null, null);
        Assert.Equal(
            [lowerFolderId, higherFolderId],
            rootFolders.Folders
                .Where(item => item.Name.StartsWith("ordered-folder-", StringComparison.Ordinal))
                .Select(item => item.Id));

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

    private sealed record RecordedCommand(string CommandText, IReadOnlyList<DbParameter> Parameters);

    /// <summary>
    /// Records every database round trip with a copy of its parameters, so a test can count the
    /// round trips of one storage call and replay a command to inspect its query plan.
    /// </summary>
    private sealed class RecordingCommandInterceptor : DbCommandInterceptor
    {
        private readonly List<RecordedCommand> _commands = [];

        public IReadOnlyList<RecordedCommand> Commands => _commands;

        public void Clear()
        {
            _commands.Clear();
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Record(command);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<object> result,
            CancellationToken cancellationToken = default)
        {
            Record(command);
            return base.ScalarExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            Record(command);
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }

        private void Record(DbCommand command)
        {
            var parameters = command.Parameters
                .Cast<DbParameter>()
                .Select(parameter => (DbParameter)((ICloneable)parameter).Clone())
                .ToArray();

            _commands.Add(new RecordedCommand(command.CommandText, parameters));
        }
    }
}
