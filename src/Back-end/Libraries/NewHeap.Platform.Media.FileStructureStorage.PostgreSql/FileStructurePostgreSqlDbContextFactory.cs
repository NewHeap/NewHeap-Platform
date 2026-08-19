using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using NewHeap.Media.FileStructureStorage.SqlServer;

namespace NewHeap.Media.FileStructureStorage.PostgreSql;

internal sealed class FileStructurePostgreSqlDbContextFactory : IDesignTimeDbContextFactory<FileStructureDbContext>
{
    public FileStructureDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("NH_MEDIA_POSTGRESQL_CONNECTION")
            ?? "Host=localhost;Database=nh_media;Username=postgres;Password=postgres";
        var options = new DbContextOptionsBuilder<FileStructureDbContext>()
            .UseNpgsql(connectionString, builder =>
                builder.MigrationsAssembly(typeof(FileStructurePostgreSqlDbContextFactory).Assembly.FullName))
            .Options;

        return new FileStructureDbContext(options, new FileStructureDbContextOptions());
    }
}
