using Microsoft.EntityFrameworkCore;
using NewHeap.Media.FileStructureStorage.SqlServer;
using NewHeap.Media.FileStructureStorage.SqlServer.Entities;

namespace NewHeap.Media.FileStructureStorage.PostgreSql;

internal static class PostgreSqlFileStructureModelConfiguration
{
    internal static void Apply(FileStructureDbContextOptions options)
    {
        options.ConfigureProviderModel = ConfigureModel;
        options.LookupHashFactory = HashHelper.ComputePostgreSqlHash;
    }

    private static void ConfigureModel(ModelBuilder modelBuilder)
    {
        ConfigureLookupHashes<FileEntity>(modelBuilder, "IX_Files");
        ConfigureLookupHashes<FolderEntity>(modelBuilder, "IX_Folders");
    }

    private static void ConfigureLookupHashes<TEntity>(ModelBuilder modelBuilder, string indexNamePrefix)
        where TEntity : class
    {
        var entity = modelBuilder.Entity<TEntity>();
        entity.Property<string>(FileStructureDbContext.PathLookupColumn).IsRequired().HasMaxLength(256);
        entity.Property<string>(FileStructureDbContext.PathNameLookupColumn).IsRequired().HasMaxLength(256);
        entity.HasIndex(FileStructureDbContext.PathLookupColumn)
            .HasDatabaseName($"{indexNamePrefix}_PathLookup");
        entity.HasIndex(FileStructureDbContext.PathNameLookupColumn)
            .HasDatabaseName($"{indexNamePrefix}_PathNameLookup");
    }
}
