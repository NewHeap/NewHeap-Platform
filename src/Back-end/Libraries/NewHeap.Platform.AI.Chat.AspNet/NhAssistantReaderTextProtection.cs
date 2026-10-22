using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NewHeap.Platform.AI.Chat.Runtime;

namespace NewHeap.Platform.AI.Chat.AspNet;

/// <summary>
/// Protects the text a reading tool made of a file while a conversation keeps it (D32), with ASP.NET
/// Data Protection, so a database copy alone does not reveal it. Hosts with several instances must
/// share their Data Protection key ring, as for MCP server secrets; a text that can no longer be
/// unprotected is read by the reader again.
/// </summary>
internal sealed class NhAssistantReaderTextProtector(IDataProtectionProvider provider) : INhAssistantReaderTextProtector
{
    private readonly IDataProtector _protector = provider.CreateProtector("NewHeap.Platform.AI.Chat.ReaderText.v1");

    public string Protect(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return _protector.Protect(text);
    }

    public string? Unprotect(string protectedText)
    {
        try
        {
            return _protector.Unprotect(protectedText);
        }
        catch (CryptographicException)
        {
            return null;
        }
    }
}

/// <summary>
/// Removes the texts of reading tools that expired (<see cref="NhAssistantLimits.ReaderTextRetention"/>)
/// once per maintenance interval, at least a minute apart, in batches. A turn never reads an expired
/// text, so the cleanup only bounds what stays stored. Every delete is conditional, so several
/// instances can run it at the same time.
/// </summary>
internal sealed partial class NhAssistantReaderTextMaintenance(
    IServiceScopeFactory scopeFactory,
    NhAssistantRegistrationState registration,
    ILogger<NhAssistantReaderTextMaintenance> logger) : BackgroundService
{
    /// <summary>
    /// The most batches one run removes, so a large backlog is spread over several runs.
    /// </summary>
    internal const int MaxBatchesPerRun = 10;

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
                await RunOnceAsync(DateTimeOffset.UtcNow, stoppingToken);
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

    /// <summary>
    /// Removes the texts that expired before <paramref name="now"/>; returns how many.
    /// </summary>
    internal async Task<int> RunOnceAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var texts = scope.ServiceProvider.GetRequiredService<NhAssistantReaderTexts>();
        var removed = 0;
        for (var batch = 0; batch < MaxBatchesPerRun; batch++)
        {
            var count = await texts.RemoveExpiredAsync(now, cancellationToken);
            removed += count;
            if (count < NhAssistantReaderTexts.CleanupBatchSize)
            {
                break;
            }
        }

        if (removed > 0)
        {
            LogRemoved(logger, removed);
        }

        return removed;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Assistant maintenance removed {Count} expired reader texts.")]
    private static partial void LogRemoved(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Removing expired reader texts failed ({ExceptionType}); it runs again at the next interval.")]
    private static partial void LogMaintenanceFailed(ILogger logger, string exceptionType);
}
