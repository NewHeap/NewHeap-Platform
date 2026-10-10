using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NewHeap.Media.FileStructureStorage;
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
        SqlServerFileStructureModelConfiguration.Apply(options);
        
        services.AddSingleton(options);
        
        services.AddDbContextPool<FileStructureDbContext>(opt =>
        {
            opt.ReplaceService<IModelCacheKeyFactory, FileStructureModelCacheKeyFactory>();
            opt.ReplaceService<IMigrationsSqlGenerator, SqlServerMediaMigrationsSqlGenerator>();

            // The checked-in snapshot uses the default schema; a custom schema is an intentional model difference.
            if (options.RunMigrations || options.Scheme != FileStructureModelCacheKeyFactory.DefaultSchema)
            {
                opt.ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning));
            }

            opt.ConfigureWarnings(logConf => logConf.Log(
                (RelationalEventId.CommandExecuting, LogLevel.Trace),
                (RelationalEventId.CommandExecuted, LogLevel.Debug),
                (CoreEventId.ContextInitialized, LogLevel.Trace)
            ));
            
            opt.UseSqlServer(connectionString, efOptions =>
            {
                var scheme = string.IsNullOrWhiteSpace(options.Scheme) ? "medialibrary" : options.Scheme;
                efOptions.MigrationsHistoryTable("_migrations", scheme);
                efOptions.MigrationsAssembly(typeof(SqlServerFileStructureStorage).Assembly.FullName);
            });
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
