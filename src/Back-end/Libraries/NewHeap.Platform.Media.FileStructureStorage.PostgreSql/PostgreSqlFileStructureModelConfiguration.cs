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
        entity.Property<byte[]>(FileStructureDbContext.PathLookupHashColumn).IsRequired().HasMaxLength(32);
        entity.Property<byte[]>(FileStructureDbContext.PathNameLookupHashColumn).IsRequired().HasMaxLength(32);
        entity.HasIndex(FileStructureDbContext.PathLookupHashColumn)
            .HasDatabaseName($"{indexNamePrefix}_PathLookupHash");
        entity.HasIndex(FileStructureDbContext.PathNameLookupHashColumn)
            .HasDatabaseName($"{indexNamePrefix}_PathNameLookupHash");
    }
}
