using Microsoft.EntityFrameworkCore;
using NewHeap.Media.FileStructureStorage.SqlServer;
using NewHeap.Media.FileStructureStorage.SqlServer.Entities;

namespace NewHeap.Media.FileStructureStorage.PostgreSql;

internal static class PostgreSqlFileStructureModelConfiguration
{
    internal const string PathLookupHashColumn = "PathLookupHash";
    internal const string PathNameLookupHashColumn = "PathNameLookupHash";

    internal static void Apply(FileStructureDbContextOptions options)
    {
        options.ConfigureProviderModel = ConfigureModel;
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
        entity.Property<byte[]>(PathLookupHashColumn).IsRequired().HasMaxLength(16);
        entity.Property<byte[]>(PathNameLookupHashColumn).IsRequired().HasMaxLength(16);
        entity.HasIndex(PathLookupHashColumn)
            .HasDatabaseName($"{indexNamePrefix}_PathLookupHash");
        entity.HasIndex(PathNameLookupHashColumn)
            .HasDatabaseName($"{indexNamePrefix}_PathNameLookupHash");
    }
}
