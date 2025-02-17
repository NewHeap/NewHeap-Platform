using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;
using NewHeap.Platform.AspNet.Common.Extensions;

namespace NewHeap.Platform.AspNet.Common.DAL;

public abstract partial class NhDbContextFactory<TDBContext> : IDesignTimeDbContextFactory<TDBContext>
    where TDBContext : NhDbContext
{
    protected DbContextOptionsBuilder<TDBContext> CreateBuilder()
    {
        IConfigurationRoot configuration = new ConfigurationBuilder()
        .ConfigureNhAspNetCommonConfiguration()
        .Build();

        var builder = new DbContextOptionsBuilder<TDBContext>();
        var connectionString = configuration.GetConnectionString("DefaultConnection");

        builder.UseSqlServer(connectionString, opts => opts.CommandTimeout((int)TimeSpan.FromMinutes(10).TotalSeconds));

        return builder;
    }

    public abstract TDBContext CreateDbContext(string[] args);
}
