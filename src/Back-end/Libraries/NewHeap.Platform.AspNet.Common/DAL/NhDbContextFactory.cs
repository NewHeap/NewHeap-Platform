using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;

namespace NewHeap.Platform.AspNet.Common.DAL;

public abstract partial class NhDbContextFactory<TDBContext> : IDesignTimeDbContextFactory<TDBContext>
    where TDBContext : NhIdentityDbContext
{
    public abstract TDBContext CreateDbContext(string[] args);

    protected virtual IConfigurationRoot CreateConfigurationRoot()
    {
        return CreateConfigurationRoot(
            basePath: Directory.GetCurrentDirectory()
        );
    }

    protected virtual IConfigurationRoot CreateConfigurationRoot(
        string basePath,
        string appSettingsFileName = "appsettings",
        string secretsFileName = "secrets")
    {
        var configuration = new ConfigurationBuilder()
            .ConfigureNewHeapAspNetCommonConfiguration(
                basePath: basePath,
                appSettingsFileName: appSettingsFileName,
                secretsFileName: secretsFileName
            )
            .Build();

        return configuration;
    }

    protected virtual DbContextOptionsBuilder<TDBContext> CreateBuilder()
    {
        DbContextOptionsBuilder<TDBContext> builder = new();

        return builder;
    }
}