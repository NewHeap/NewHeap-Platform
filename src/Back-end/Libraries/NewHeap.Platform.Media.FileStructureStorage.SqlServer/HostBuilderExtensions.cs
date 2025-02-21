using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NewHeap.Media.FileStructureStorage.SqlServer;
using NewHeap.Media.Modules;

// ReSharper disable once CheckNamespace
namespace NewHeap.Media;

public static class HostBuilderExtensions
{
    public static IServiceCollection AddMediaSqlServerStorage(
        this IServiceCollection services,
        string connectionString, 
        Action<FileStructureDbContextOptions>? configureDbSet = null
    )
    {
        var options = new FileStructureDbContextOptions();
        if (configureDbSet != null)
        {
            configureDbSet(options);
        }
        
        services.AddSingleton(options);
        
        services.AddDbContextPool<FileStructureDbContext>(opt =>
        {
            opt.UseSqlServer(connectionString);
        });

        if (options.RunMigrations)
        {
            services.AddHostedService<MigrateDatabaseHostedService>();
        }

        services.AddTransient<IFileStructureStorage, SqlServerFileStructureStorage>();
        
        return services;
    }
    
    
    public static IApplicationBuilder UseMediaSqlServerStorage(this IApplicationBuilder app)
    {
        return app;
    }
}