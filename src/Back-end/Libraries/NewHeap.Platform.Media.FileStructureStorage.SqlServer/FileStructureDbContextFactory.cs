using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace NewHeap.Media.FileStructureStorage.SqlServer;

internal class FileStructureDbContextFactory : IDesignTimeDbContextFactory<FileStructureDbContext>
{
    public FileStructureDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("NH_MEDIA_SQLSERVER_CONNECTION")
            ?? "Server=localhost;Database=nh_media;Integrated Security=true;TrustServerCertificate=true";
        var builder = new DbContextOptionsBuilder<FileStructureDbContext>()
            .UseSqlServer(connectionString, options =>
                options.MigrationsAssembly(typeof(FileStructureDbContextFactory).Assembly.FullName));

        var storageOptions = new FileStructureDbContextOptions();
        SqlServerFileStructureModelConfiguration.Apply(storageOptions);
        return new FileStructureDbContext(builder.Options, storageOptions);
    }
}
