using Microsoft.EntityFrameworkCore;
using NewHeap.Media.FileStructureStorage.SqlServer.Entities;

namespace NewHeap.Media.FileStructureStorage.SqlServer;

internal static class SqlServerFileStructureModelConfiguration
{
    internal static void Apply(FileStructureDbContextOptions options)
    {
        options.ConfigureProviderModel = ConfigureModel;
        options.LookupHashFactory = null;
    }

    private static void ConfigureModel(ModelBuilder modelBuilder)
    {
        // Preserve the explicit type used by the existing SQL Server migrations.
        // SQL Server treats the casing identically, but EF compares the store type textually.
        modelBuilder.Entity<FileEntity>()
            .Property(x => x.MetaData)
            .HasColumnType("NVARCHAR(MAX)");
        ConfigureLookupHashes<FileEntity>(modelBuilder, nameof(FileEntity.Id), "IX_Files");
        ConfigureLookupHashes<FolderEntity>(modelBuilder, nameof(FolderEntity.Id), "IX_Folders");
    }

    private static void ConfigureLookupHashes<TEntity>(ModelBuilder modelBuilder,
        string idPropertyName, string indexNamePrefix)
        where TEntity : class
    {
        var entity = modelBuilder.Entity<TEntity>();
        entity.Property<byte[]>(FileStructureDbContext.PathLookupHashColumn)
            .HasColumnType("binary(32)")
            .HasComputedColumnSql(FileStructureDbContext.PathLookupHashSql, stored: true);
        entity.Property<byte[]>(FileStructureDbContext.PathNameLookupHashColumn)
            .HasColumnType("binary(32)")
            .HasComputedColumnSql(FileStructureDbContext.PathNameLookupHashSql, stored: true);

        entity.HasIndex(FileStructureDbContext.PathLookupHashColumn)
            .HasDatabaseName($"{indexNamePrefix}_PathLookupHash")
            .IncludeProperties(idPropertyName);
        entity.HasIndex(FileStructureDbContext.PathNameLookupHashColumn)
            .HasDatabaseName($"{indexNamePrefix}_PathNameLookupHash")
            .IncludeProperties(idPropertyName);
    }
}
