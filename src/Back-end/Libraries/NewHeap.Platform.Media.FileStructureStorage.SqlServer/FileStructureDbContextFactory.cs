using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace NewHeap.Media.FileStructureStorage.SqlServer;

internal class FileStructureDbContextFactory : IDesignTimeDbContextFactory<FileStructureDbContext>
{
    public FileStructureDbContext CreateDbContext(string[] args)
    {
        var connectionString = "Server=hb-win-db-1.hostbusters.nl;Initial Catalog=nh-media-dev;User Id=nh-media-dev;Password=NewHeap123!;";
        var builder = new DbContextOptionsBuilder<FileStructureDbContext>()
            .UseSqlServer(connectionString);

        return new FileStructureDbContext(builder.Options);
    }
}