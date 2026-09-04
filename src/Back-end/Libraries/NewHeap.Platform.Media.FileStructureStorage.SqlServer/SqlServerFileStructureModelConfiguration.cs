using Microsoft.EntityFrameworkCore;
using NewHeap.Media.FileStructureStorage.SqlServer.Entities;

namespace NewHeap.Media.FileStructureStorage.SqlServer;

internal static class SqlServerFileStructureModelConfiguration
{
    internal const string PathLookupColumn = "PathLookup";
    internal const string PathNameLookupColumn = "PathNameLookup";

    private const string PathLookupSql =
        "CONVERT(nvarchar(256), LOWER(COALESCE([Path], N'')))";

    private const string PathNameLookupSql =
        "CONVERT(nvarchar(256), LOWER(COALESCE([Path], N'')) + NCHAR(31) + LOWER([Name]))";

    internal static void Apply(FileStructureDbContextOptions options)
    {
        options.ConfigureProviderModel = ConfigureModel;
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
        entity.Property<string>(PathLookupColumn)
            .HasColumnType("NVARCHAR(256)")
            .HasComputedColumnSql(PathLookupSql, stored: true);
        entity.Property<string>(PathNameLookupColumn)
            .HasColumnType("NVARCHAR(256)")
            .HasComputedColumnSql(PathNameLookupSql, stored: true);

        entity.HasIndex(PathLookupColumn)
            .HasDatabaseName($"{indexNamePrefix}_PathLookup")
            .IncludeProperties(idPropertyName);
        entity.HasIndex(PathNameLookupColumn)
            .HasDatabaseName($"{indexNamePrefix}_PathNameLookup")
            .IncludeProperties(idPropertyName);
    }
}
