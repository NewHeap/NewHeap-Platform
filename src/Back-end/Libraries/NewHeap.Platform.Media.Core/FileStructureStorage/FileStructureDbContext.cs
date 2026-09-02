using Microsoft.EntityFrameworkCore;
using NewHeap.Media.FileStructureStorage.SqlServer.Entities;

namespace NewHeap.Media.FileStructureStorage.SqlServer;

public class FileStructureDbContext : DbContext
{

    public const string PathNameLookupSql =
        "CONVERT(nvarchar(256), LOWER(COALESCE([Path], N'')) + NCHAR(31) + LOWER([Name]))";
    
    public const string PathLookupSql =
        "CONVERT(nvarchar(256), LOWER(COALESCE([Path], N'')) + NCHAR(31) + LOWER([Name]))";

    public const string PathNameLookupColumn = "PathNameLookup";
    public const string PathLookupColumn = "PathLookup";
    

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

        modelBuilder.Entity<Localization>(e =>
        {
            e.HasKey(x => new { x.TypeName, x.EntityId, x.Language, x.PropertyName });
            e.HasIndex(x => new { x.TypeName, x.EntityId, x.Language });
        });

        if (_dbContextOptions.ConfigureProviderModel is null)
        {
            throw new InvalidOperationException("A relational media provider must configure the file-structure model.");
        }
        _dbContextOptions.ConfigureProviderModel(modelBuilder);
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

    private void PopulatePostgreSqlLookupHashes()
    {
        var lookupHashFactory = _dbContextOptions.LookupHashFactory;
        if (lookupHashFactory is null)
        {
            return;
        }

        foreach (var entry in ChangeTracker.Entries<FileEntity>()
                     .Where(x => x.State is EntityState.Added or EntityState.Modified))
        {
            SetLookupHashes(entry, lookupHashFactory, entry.Entity.Path, entry.Entity.Name);
        }

        foreach (var entry in ChangeTracker.Entries<FolderEntity>()
                     .Where(x => x.State is EntityState.Added or EntityState.Modified))
        {
            SetLookupHashes(entry, lookupHashFactory, entry.Entity.Path, entry.Entity.Name);
        }
    }

    private static void SetLookupHashes<TEntity>(Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry<TEntity> entry,
        Func<string?[], byte[]> lookupHashFactory, string? path, string? name)
        where TEntity : class
    {
        entry.Property<byte[]>(PathLookupColumn).CurrentValue = lookupHashFactory([path]);
        entry.Property<byte[]>(PathNameLookupColumn).CurrentValue = lookupHashFactory([path, name]);
    }
}
