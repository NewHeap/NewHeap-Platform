using Microsoft.EntityFrameworkCore;
using NewHeap.Media.FileStructureStorage.SqlServer.Entities;

namespace NewHeap.Media.FileStructureStorage.SqlServer;

public class FileStructureDbContext : DbContext
{
    private const string PostgreSqlProviderName = "Npgsql.EntityFrameworkCore.PostgreSQL";
    // Path and Name exceed SQL Server's practical index-key limits, so lookups seek on persisted hashes.
    public const string PathLookupHashColumn = "PathLookupHash";
    public const string PathNameLookupHashColumn = "PathNameLookupHash";

    public const string PathLookupHashSql =
        "CONVERT(binary(32), HASHBYTES('SHA2_256', LOWER(COALESCE([Path], N''))))";

    public const string PathNameLookupHashSql =
        "CONVERT(binary(32), HASHBYTES('SHA2_256', CONCAT(LOWER(COALESCE([Path], N'')), NCHAR(31), LOWER(COALESCE([Name], N'')))))";

    private readonly FileStructureDbContextOptions _dbContextOptions;
    public DbSet<FileEntity> Files { get; set; }
    public DbSet<FolderEntity> Folders { get; set; }
    
    public DbSet<Localization> Localizations { get; set; }

    public FileStructureDbContext(DbContextOptions<FileStructureDbContext> options, FileStructureDbContextOptions dbContextOptions) : base(options)
    {
        _dbContextOptions = dbContextOptions;
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(_dbContextOptions.Scheme);
        var isPostgreSql = string.Equals(Database.ProviderName, PostgreSqlProviderName, StringComparison.Ordinal);

        modelBuilder.Entity<FileEntity>(e =>
        {
            ConfigureLookupHashes(e, nameof(FileEntity.Id), "IX_Files", isPostgreSql);
        });

        modelBuilder.Entity<FolderEntity>(e =>
        {
            ConfigureLookupHashes(e, nameof(FolderEntity.Id), "IX_Folders", isPostgreSql);
        });

        modelBuilder.Entity<Localization>(e =>
        {
            e.HasKey(x => new { x.TypeName, x.EntityId, x.Language, x.PropertyName });
            e.HasIndex(x => new { x.TypeName, x.EntityId, x.Language });
        });
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        PopulatePostgreSqlLookupHashes();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess,
        CancellationToken cancellationToken = default)
    {
        PopulatePostgreSqlLookupHashes();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    private void ConfigureLookupHashes<TEntity>(Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<TEntity> entity,
        string idPropertyName, string indexNamePrefix, bool isPostgreSql)
        where TEntity : class
    {
        var pathLookupHash = entity.Property<byte[]>(PathLookupHashColumn);
        var pathNameLookupHash = entity.Property<byte[]>(PathNameLookupHashColumn);

        if (isPostgreSql)
        {
            pathLookupHash.IsRequired().HasMaxLength(32);
            pathNameLookupHash.IsRequired().HasMaxLength(32);
        }
        else
        {
            pathLookupHash
                .HasColumnType("binary(32)")
                .HasComputedColumnSql(PathLookupHashSql, stored: true);
            pathNameLookupHash
                .HasColumnType("binary(32)")
                .HasComputedColumnSql(PathNameLookupHashSql, stored: true);
        }

        var pathIndex = entity.HasIndex(PathLookupHashColumn)
            .HasDatabaseName($"{indexNamePrefix}_PathLookupHash");
        var pathNameIndex = entity.HasIndex(PathNameLookupHashColumn)
            .HasDatabaseName($"{indexNamePrefix}_PathNameLookupHash");

        if (!isPostgreSql)
        {
            pathIndex.IncludeProperties(idPropertyName);
            pathNameIndex.IncludeProperties(idPropertyName);
        }
    }

    private void PopulatePostgreSqlLookupHashes()
    {
        if (!string.Equals(Database.ProviderName, PostgreSqlProviderName, StringComparison.Ordinal))
        {
            return;
        }

        foreach (var entry in ChangeTracker.Entries<FileEntity>()
                     .Where(x => x.State is EntityState.Added or EntityState.Modified))
        {
            SetLookupHashes(entry, entry.Entity.Path, entry.Entity.Name);
        }

        foreach (var entry in ChangeTracker.Entries<FolderEntity>()
                     .Where(x => x.State is EntityState.Added or EntityState.Modified))
        {
            SetLookupHashes(entry, entry.Entity.Path, entry.Entity.Name);
        }
    }

    private static void SetLookupHashes<TEntity>(Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry<TEntity> entry,
        string? path, string? name)
        where TEntity : class
    {
        entry.Property<byte[]>(PathLookupHashColumn).CurrentValue = HashHelper.ComputePostgreSqlHash(path);
        entry.Property<byte[]>(PathNameLookupHashColumn).CurrentValue = HashHelper.ComputePostgreSqlHash(path, name);
    }
}
