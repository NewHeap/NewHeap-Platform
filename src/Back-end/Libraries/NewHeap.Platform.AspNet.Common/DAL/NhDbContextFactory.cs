using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;

namespace NewHeap.Platform.AspNet.Common.DAL;

public abstract partial class NhDbContextFactory<TDBContext> : IDesignTimeDbContextFactory<TDBContext>
    where TDBContext : NhDbContext
{
    protected IConfigurationRoot CreateConfigurationRoot()
    {
        IConfigurationRoot configuration = new ConfigurationBuilder()
           .ConfigureNhAspNetCommonConfiguration()
           .Build();

        return configuration;
    }

    protected DbContextOptionsBuilder<TDBContext> CreateBuilder()
    {
        var builder = new DbContextOptionsBuilder<TDBContext>();

        return builder;
    }

    public abstract TDBContext CreateDbContext(string[] args);
}
