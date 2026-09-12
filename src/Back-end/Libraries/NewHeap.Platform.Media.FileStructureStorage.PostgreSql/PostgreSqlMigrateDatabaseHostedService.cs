using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NewHeap.Media.FileStructureStorage.SqlServer;

namespace NewHeap.Media.FileStructureStorage.PostgreSql;

internal sealed class PostgreSqlMigrateDatabaseHostedService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<PostgreSqlMigrateDatabaseHostedService> _logger;

    public PostgreSqlMigrateDatabaseHostedService(
        IServiceProvider serviceProvider,
        ILogger<PostgreSqlMigrateDatabaseHostedService> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Migrating database for PostgreSQL FileStructureStorage");
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<FileStructureDbContext>();

            await dbContext.Database.MigrateAsync(stoppingToken);
            _logger.LogInformation("Migration completed for PostgreSQL FileStructureStorage");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception when migrating PostgreSQL FileStructureStorage");
        }
    }
}
