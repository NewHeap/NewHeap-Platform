using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace NewHeap.Media.FileStructureStorage.SqlServer;

internal class MigrateDatabaseHostedService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<MigrateDatabaseHostedService> _logger;

    public MigrateDatabaseHostedService(IServiceProvider serviceProvider, ILogger<MigrateDatabaseHostedService> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }
    
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Migrating database for SqlServer FileStructureStorage");
        using var scope = _serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<FileStructureDbContext>();
        await dbContext.Database.MigrateAsync(stoppingToken);
        _logger.LogInformation("Migration completed for SqlServer FileStructureStorage");
    }
}