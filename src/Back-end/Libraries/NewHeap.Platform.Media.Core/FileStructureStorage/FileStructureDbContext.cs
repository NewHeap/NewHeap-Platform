using Microsoft.EntityFrameworkCore;
using NewHeap.Media.FileStructureStorage.SqlServer.Entities;

namespace NewHeap.Media.FileStructureStorage.SqlServer;

public class FileStructureDbContext : DbContext
{
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

}
