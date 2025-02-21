using Microsoft.EntityFrameworkCore;
using NewHeap.Media.FileStructureStorage.SqlServer.Entities;

namespace NewHeap.Media.FileStructureStorage.SqlServer;

internal class FileStructureDbContext : DbContext
{
    public DbSet<FileEntity> Files { get; set; }
    public DbSet<FolderEntity> Folders { get; set; }
    
    public DbSet<Localization> Localizations { get; set; }

    public FileStructureDbContext() : base()
    {
        
    }

    public FileStructureDbContext(DbContextOptions<FileStructureDbContext> options) : base(options)
    {
        
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Localization>(e =>
        {
            e.HasKey(x => new { x.TypeName, x.EntityId, x.Language, x.PropertyName });
            e.HasIndex(x => new { x.TypeName, x.EntityId, x.Language });
        });
    }
}