using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NewHeap.Media.FileStructureStorage.PostgreSql;
using NewHeap.Media.FileStructureStorage.SqlServer;
using NewHeap.Media.Modules;

// ReSharper disable once CheckNamespace
namespace NewHeap.Media;

public static class PostgreSqlServiceCollectionExtensions
{
    public static IServiceCollection AddMediaPostgreSqlStorage(
        this IServiceCollection services,
        string connectionString,
        Action<FileStructureDbContextOptions>? configureDbSet = null)
    {
        var options = new FileStructureDbContextOptions();
        configureDbSet?.Invoke(options);
        if (string.IsNullOrWhiteSpace(options.Scheme))
        {
            options.Scheme = "nhmedia";
        }
        PostgreSqlFileStructureModelConfiguration.Apply(options);
        var lookupHashInterceptor = new PostgreSqlLookupHashSaveChangesInterceptor();

        services.AddSingleton(options);
        services.AddDbContextPool<FileStructureDbContext>(opt =>
        {
            opt.ReplaceService<IModelCacheKeyFactory, PostgreSqlMediaModelCacheKeyFactory>();
            opt.ReplaceService<IMigrationsSqlGenerator, PostgreSqlMediaMigrationsSqlGenerator>();
            opt.AddInterceptors(lookupHashInterceptor);

            // The checked-in snapshot uses the default schema; a custom schema is an intentional model difference.
            if (options.RunMigrations || options.Scheme != "nhmedia")
            {
                opt.ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning));
            }

            opt.ConfigureWarnings(logConf => logConf.Log(
                (RelationalEventId.CommandExecuting, LogLevel.Trace),
                (RelationalEventId.CommandExecuted, LogLevel.Debug),
                (CoreEventId.ContextInitialized, LogLevel.Trace)
            ));

            opt.UseNpgsql(connectionString, efOptions =>
            {
                efOptions.MigrationsHistoryTable("_migrations", options.Scheme);
                efOptions.MigrationsAssembly(typeof(PostgreSqlFileStructureStorage).Assembly.FullName);
            });
        });

        if (options.RunMigrations)
        {
            services.AddHostedService<PostgreSqlMigrateDatabaseHostedService>();
        }

        services.AddTransient<IFileStructureStorage, PostgreSqlFileStructureStorage>();
        return services;
    }

    public static IApplicationBuilder UseMediaPostgreSqlStorage(this IApplicationBuilder app)
    {
        return app;
    }
}
