using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;

namespace NewHeap.Platform.AspNet.Common.DAL;

public abstract partial class NhDbContextFactory<TDBContext> : IDesignTimeDbContextFactory<TDBContext>
    where TDBContext : NhDbContext
{
    public abstract TDBContext CreateDbContext(string[] args);

    protected IConfigurationRoot CreateConfigurationRoot()
    {
        var configuration = new ConfigurationBuilder()
            .ConfigureNewHeapAspNetCommonConfiguration()
            .Build();

        return configuration;
    }

    protected DbContextOptionsBuilder<TDBContext> CreateBuilder()
    {
        DbContextOptionsBuilder<TDBContext> builder = new();

        return builder;
    }
}