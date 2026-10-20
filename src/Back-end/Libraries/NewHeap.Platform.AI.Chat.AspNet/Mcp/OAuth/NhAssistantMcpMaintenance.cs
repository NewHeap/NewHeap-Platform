using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NewHeap.Platform.AI.Chat.Persistence;

namespace NewHeap.Platform.AI.Chat.AspNet.Mcp;

/// <summary>
/// Removes expired authorizations, with their protected verifiers and token responses, the personal
/// connections of people the platform no longer resolves (<see cref="NhAssistantMcpOwnerSweep"/>) and
/// personal OAuth connections unused for <see cref="NhAssistantMcpOAuthOptions.ConnectionRetention"/>.
/// The owner sweep checks <see cref="OwnerBatchSize"/> people per run and starts a pass over all of
/// them at most every <see cref="OwnerPassInterval"/>, the first one interval after start-up. Every
/// change is conditional, so several instances can run it at the same time.
/// </summary>
internal sealed partial class NhAssistantMcpMaintenance(
    IServiceScopeFactory scopeFactory,
    NhAssistantRegistrationState registration,
    ILogger<NhAssistantMcpMaintenance> logger) : BackgroundService
{
    private const int BatchSize = 100;

    /// <summary>The people the owner sweep checks per run.</summary>
    internal const int OwnerBatchSize = 100;

    /// <summary>How often a pass of the owner sweep over everyone with a personal connection starts.</summary>
    internal static readonly TimeSpan OwnerPassInterval = TimeSpan.FromHours(1);

    // The owner sweep's place in its pass: null between passes, "" at the start of one.
    private string? _ownerCursor;
    private DateTimeOffset _nextOwnerPass = DateTimeOffset.UtcNow + OwnerPassInterval;

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
                LogMaintenanceFailed(logger, exception);
            }
        }
    }

    internal Task RunOnceAsync(CancellationToken cancellationToken)
    {
        return RunOnceAsync(DateTimeOffset.UtcNow, cancellationToken);
    }

    /// <summary>One maintenance run at <paramref name="now"/>, which decides whether an owner pass is due.</summary>
    internal async Task RunOnceAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var services = scope.ServiceProvider;
        await services.GetRequiredService<NhAssistantMcpOAuthStore>()
            .DeleteFlowsExpiredBeforeAsync(DateTimeOffset.UtcNow, cancellationToken);
        await SweepOwnersAsync(services, now, cancellationToken);
        var servers = services.GetRequiredService<NhAssistantAdminStore>();
        await services.GetRequiredService<NhAssistantMcpOAuthConnections>()
            .RemoveStaleAsync(servers.FindMcpServerAsync, BatchSize, cancellationToken);
    }

    /// <summary>
    /// Runs the next batch of the owner sweep when a pass is under way or due. A failure is logged and
    /// leaves the place in the pass, so retention still runs and the next run tries the batch again.
    /// </summary>
    private async Task SweepOwnersAsync(IServiceProvider services, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (_ownerCursor is null)
        {
            if (now < _nextOwnerPass)
            {
                return;
            }
            _nextOwnerPass = now + OwnerPassInterval;
            _ownerCursor = string.Empty;
        }

        try
        {
            var batch = await services.GetRequiredService<NhAssistantMcpOwnerSweep>()
                .RunBatchAsync(_ownerCursor, OwnerBatchSize, cancellationToken);
            _ownerCursor = batch.Checked < OwnerBatchSize ? null : batch.LastOwner;
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
        {
            LogOwnerSweepFailed(logger, exception);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The assistant's MCP owner sweep failed; it continues at the next maintenance run.")]
    private static partial void LogOwnerSweepFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Assistant MCP maintenance failed; it runs again at the next interval.")]
    private static partial void LogMaintenanceFailed(ILogger logger, Exception exception);
}
