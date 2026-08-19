using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NewHeap.Media.FileStructureStorage.SqlServer.Migrations;

namespace NewHeap.Media.FileStructureStorage.SqlServer;

internal class MigrateDatabaseHostedService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<MigrateDatabaseHostedService> _logger;
    private readonly FileStructureDbContextOptions _dbContextOptions;

    public MigrateDatabaseHostedService(
        IServiceProvider serviceProvider,
        ILogger<MigrateDatabaseHostedService> logger,
        FileStructureDbContextOptions dbContextOptions
        )
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        _dbContextOptions = dbContextOptions;
    }
    
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Migrating database for SqlServer FileStructureStorage");
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<FileStructureDbContext>();

            BaseMigration.DefaultScheme = _dbContextOptions.Scheme;
            await dbContext.Database.MigrateAsync(stoppingToken);
            _logger.LogInformation("Migration completed for SqlServer FileStructureStorage");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception when migrating FileStructureStorage");
        }
    }
}