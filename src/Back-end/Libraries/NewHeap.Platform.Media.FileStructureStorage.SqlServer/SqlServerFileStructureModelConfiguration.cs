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
        entity.Property<string>(FileStructureDbContext.PathLookupColumn)
            .HasColumnType("NVARCHAR(256)")
            .HasComputedColumnSql(FileStructureDbContext.PathLookupSql, stored: true);
        entity.Property<string>(FileStructureDbContext.PathNameLookupColumn)
            .HasColumnType("NVARCHAR(256)")
            .HasComputedColumnSql(FileStructureDbContext.PathNameLookupSql, stored: true);

        entity.HasIndex(FileStructureDbContext.PathLookupColumn)
            .HasDatabaseName($"{indexNamePrefix}_PathLookup")
            .IncludeProperties(idPropertyName);
        entity.HasIndex(FileStructureDbContext.PathNameLookupColumn)
            .HasDatabaseName($"{indexNamePrefix}_PathNameLookup")
            .IncludeProperties(idPropertyName);
    }
}
