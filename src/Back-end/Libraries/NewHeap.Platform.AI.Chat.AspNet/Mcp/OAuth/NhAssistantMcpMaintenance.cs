using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NewHeap.Platform.AI.Chat.Persistence;

namespace NewHeap.Platform.AI.Chat.AspNet.Mcp;

/// <summary>
/// Removes expired authorizations, with their protected verifiers and token responses, and personal
/// OAuth connections unused for <see cref="NhAssistantMcpOAuthOptions.ConnectionRetention"/>. Every
/// change is a conditional delete, so several instances can run it at the same time.
/// </summary>
internal sealed partial class NhAssistantMcpMaintenance(
    IServiceScopeFactory scopeFactory,
    NhAssistantRegistrationState registration,
    ILogger<NhAssistantMcpMaintenance> logger) : BackgroundService
{
    private const int BatchSize = 100;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = registration.Limits.MaintenanceInterval < TimeSpan.FromMinutes(1)
            ? TimeSpan.FromMinutes(1)
            : registration.Limits.MaintenanceInterval;
        using var timer = new PeriodicTimer(interval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                LogMaintenanceFailed(logger, exception.GetType().Name);
            }
        }
    }

    internal async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var services = scope.ServiceProvider;
        await services.GetRequiredService<NhAssistantMcpOAuthStore>()
            .DeleteFlowsExpiredBeforeAsync(DateTimeOffset.UtcNow, cancellationToken);
        var servers = services.GetRequiredService<NhAssistantAdminStore>();
        await services.GetRequiredService<NhAssistantMcpOAuthConnections>()
            .RemoveStaleAsync(servers.FindMcpServerAsync, BatchSize, cancellationToken);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Assistant MCP maintenance failed with {ExceptionType}; it runs again at the next interval.")]
    private static partial void LogMaintenanceFailed(ILogger logger, string exceptionType);
}
