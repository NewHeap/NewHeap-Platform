using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
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
        PostgreSqlFileStructureModelConfiguration.Apply(options);
        var lookupHashInterceptor = new PostgreSqlLookupHashSaveChangesInterceptor();

        services.AddSingleton(options);
        services.AddDbContextPool<FileStructureDbContext>(opt =>
        {
            opt.AddInterceptors(lookupHashInterceptor);

            if (options.RunMigrations)
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
                var scheme = string.IsNullOrWhiteSpace(options.Scheme) ? "medialibrary" : options.Scheme;
                efOptions.MigrationsHistoryTable("_migrations", scheme);
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
