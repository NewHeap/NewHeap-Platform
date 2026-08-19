using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NewHeap.Platform.AspNet.Common.DAL;
using NewHeap.Platform.AspNet.Common.DAL.Entities;
using NewHeap.Platform.Common.Models;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace NewHeap.Platform.AspNet.Common.Services.Notification;

public class NhNotificationSettings
{
    public string ProcessorKey { get; set; } = "default";
    public bool ProcessingEnabled { get; set; } = true;
    public TimeSpan ProcessingLockTimeout { get;set; } = TimeSpan.FromMinutes(1);
    public TimeSpan ProcessingPollerLockTimeout { get; set; } = TimeSpan.Zero;
    public int ProcessingMaxRetryAttempts { get; set; } = 3;
    public TimeSpan ProcessingCleanupInterval { get; set; } = TimeSpan.FromHours(1);
    public TimeSpan ProcessingRetentionPeriod { get; set; } = TimeSpan.FromDays(30);
    /// <summary>
    /// Configures the number of workers per dispatcher. Dispatchers that are not listed use one worker.
    /// Worker counts are created when the notification processor starts.
    /// </summary>
    public Dictionary<string, int> ProcessingDispatcherConcurrency { get; set; } = new();

}

internal class NhNotificationProcessingService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IDictionary<string, Type> _dispatchers
        = new Dictionary<string, Type>();
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<Guid, int>> _inFlight
        = new ConcurrentDictionary<string, ConcurrentDictionary<Guid, int>>();
    private readonly ILogger<NhNotificationProcessingService> _logger;
    private readonly TimeSpan _idleDelay;
    private readonly TimeSpan _retryDelay;
    private NhNotificationSettings _settings;

    internal sealed record NotificationDeliveryClaim(Guid DeliveryId, int AttemptNumber);

    public NhNotificationProcessingService(
        IServiceScopeFactory scopeFactory,
        ILogger<NhNotificationProcessingService> logger,
        IOptionsMonitor<NhNotificationSettings> settingsOptionsMonitor
        )
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _idleDelay = TimeSpan.FromSeconds(5);
        _retryDelay = TimeSpan.FromSeconds(5);
        _settings = settingsOptionsMonitor.CurrentValue;
        settingsOptionsMonitor.OnChange(updated => {
            _settings = updated;
        });
    }

    internal NhNotificationProcessingService(
        IServiceScopeFactory scopeFactory,
        ILogger<NhNotificationProcessingService> logger,
        IOptionsMonitor<NhNotificationSettings> settingsOptionsMonitor,
        TimeSpan idleDelay,
        TimeSpan retryDelay
        )
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _idleDelay = idleDelay;
        _retryDelay = retryDelay;
        _settings = settingsOptionsMonitor.CurrentValue;
        settingsOptionsMonitor.OnChange(updated => {
            _settings = updated;
        });
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            SetupDispatchers();

            var processingTasks = _dispatchers
                .SelectMany(kv => Enumerable
                    .Range(0, GetDispatcherConcurrency(kv.Key))
                    .Select(workerIndex => RunDispatcherWorkerAsync(
                        kv.Key,
                        kv.Value,
                        workerIndex,
                        stoppingToken)))
                .ToArray();

            var cleanupTask = RunCleanupLoopAsync(stoppingToken);
            var unknownDispatcherTask = RunUnknownDispatcherLoopAsync(stoppingToken);

            await Task.WhenAll(processingTasks.Concat(new[] { cleanupTask, unknownDispatcherTask }));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Expected while the host is stopping.
        }
        catch(Exception ex)
        {
            _logger.LogError(ex, "Error in NhNotificationProcessingService");
        }
        
    }

    private void SetupDispatchers()
    {
        using var mainScope = _scopeFactory.CreateScope();

        var dispatchers = mainScope
            .ServiceProvider
            .GetServices<INhNotificationDispatcher>()
            .ToList();

        foreach (var disp in dispatchers)
        {
            _dispatchers[disp.DispatcherId] = disp.GetType();
            _inFlight[disp.DispatcherId] = new ConcurrentDictionary<Guid, int>();
        }
    }

    private int GetDispatcherConcurrency(string dispatcherId)
    {
        if (!_settings.ProcessingDispatcherConcurrency.TryGetValue(dispatcherId, out var concurrency))
        {
            return 1;
        }

        if (concurrency < 1)
        {
            throw new InvalidOperationException(
                $"Notification dispatcher concurrency for '{dispatcherId}' must be at least 1.");
        }

        return concurrency;
    }

    private string GetPollerLockResourceName()
    {
        return $"NhNotificationProcessing:Poller:{_settings.ProcessorKey}";
    }

    private async Task RunDispatcherWorkerAsync(
        string dispatcherId,
        Type typeOfDispatcher,
        int workerIndex,
        CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "Notification worker {WorkerIndex} for dispatcher {DispatcherId} started",
            workerIndex,
            dispatcherId);

        while (!stoppingToken.IsCancellationRequested)
        {
            if (!_settings.ProcessingEnabled)
            {
                await Task.Delay(_idleDelay, stoppingToken);
                continue;
            }

            try
            {
                var claim = await TryClaimNextDeliveryAsync(dispatcherId, stoppingToken);
                if (claim is null)
                {
                    await Task.Delay(_idleDelay, stoppingToken);
                    continue;
                }

                await DispatchClaimedDeliveryAsync(
                    dispatcherId,
                    typeOfDispatcher,
                    claim,
                    stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Notification worker {WorkerIndex} for dispatcher {DispatcherId} failed",
                    workerIndex,
                    dispatcherId);
                await Task.Delay(_idleDelay, stoppingToken);
            }
        }

        _logger.LogInformation(
            "Notification worker {WorkerIndex} for dispatcher {DispatcherId} stopped",
            workerIndex,
            dispatcherId);
    }

    internal async Task<NotificationDeliveryClaim?> TryClaimNextDeliveryAsync(
        string dispatcherId,
        CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IRepository<NhNotificationDelivery>>();
        await using var transactionScope = await repository.StartOrGetTransactionScopeAsync(cancellationToken);

        if (!await repository.TryAcquireTransactionLockAsync(
                transactionScope,
                GetPollerLockResourceName(),
                (int)Math.Max(0, _settings.ProcessingPollerLockTimeout.TotalMilliseconds),
                cancellationToken))
        {
            return null;
        }

        var now = DateTimeOffset.UtcNow;
        var staleThreshold = now - _settings.ProcessingLockTimeout;
        var activeDeliveryIds = _inFlight
            .GetOrAdd(dispatcherId, _ => new ConcurrentDictionary<Guid, int>())
            .Keys
            .ToList();

        var delivery = await repository
            .GetAll()
            .Where(x => x.Notification!.ProcessorKey == _settings.ProcessorKey)
            .Where(x => x.DispatcherId == dispatcherId)
            .Where(x =>
                (x.Status == NotificationDeliveryStatus.Queued && x.ScheduledAt <= now)
                || (x.Status == NotificationDeliveryStatus.Processing
                    && !activeDeliveryIds.Contains(x.Id)
                    && (x.LastSendAttemptAt == null || x.LastSendAttemptAt <= staleThreshold)))
            .Select(x => new
            {
                Priority = x.Priority.HasValue ? x.Priority : x.Notification!.Priority,
                Delivery = x
            })
            .OrderByDescending(x => x.Priority)
            .ThenBy(x => x.Delivery.ScheduledAt)
            .Select(x => x.Delivery)
            .FirstOrDefaultAsync(cancellationToken);

        if (delivery is null)
        {
            return null;
        }

        var maxAttempts = Math.Max(1, _settings.ProcessingMaxRetryAttempts);
        if (delivery.AttemptCount >= maxAttempts)
        {
            delivery.Status = NotificationDeliveryStatus.Failed;
            delivery.LastFailedMessage ??= "Max attempts reached while recovering notification processing.";
            await repository.SaveChangesAsync(cancellationToken);
            await transactionScope.CommitAsync(cancellationToken);
            return null;
        }

        delivery.Status = NotificationDeliveryStatus.Processing;
        delivery.LastSendAttemptAt = now;
        delivery.AttemptCount++;

        await repository.SaveChangesAsync(cancellationToken);
        await transactionScope.CommitAsync(cancellationToken);

        return new NotificationDeliveryClaim(delivery.Id, delivery.AttemptCount);
    }

    private async Task DispatchClaimedDeliveryAsync(
        string dispatcherId,
        Type typeOfDispatcher,
        NotificationDeliveryClaim claim,
        CancellationToken stoppingToken)
    {
        TaskResult dispatchResult;

        using (var scope = _scopeFactory.CreateScope())
        {
            var repository = scope.ServiceProvider.GetRequiredService<IRepository<NhNotificationDelivery>>();
            var delivery = await repository
                .GetAll()
                .Where(x => x.Notification!.ProcessorKey == _settings.ProcessorKey)
                .FirstOrDefaultAsync(x => x.Id == claim.DeliveryId, stoppingToken);

            if (delivery is null)
            {
                _logger.LogWarning(
                    "Notification delivery {DeliveryId} disappeared before attempt {AttemptNumber} could be dispatched by {DispatcherId}",
                    claim.DeliveryId,
                    claim.AttemptNumber,
                    dispatcherId);
                return;
            }

            var dispatcher = scope.ServiceProvider
                .GetServices<INhNotificationDispatcher>()
                .First(x => x.GetType() == typeOfDispatcher);

            var inFlight = _inFlight[dispatcherId];
            if (!inFlight.TryAdd(claim.DeliveryId, claim.AttemptNumber))
            {
                _logger.LogWarning(
                    "Notification delivery {DeliveryId} attempt {AttemptNumber} is already active for dispatcher {DispatcherId}",
                    claim.DeliveryId,
                    claim.AttemptNumber,
                    dispatcherId);
                return;
            }

            try
            {
                _logger.LogInformation(
                    "Dispatching notification delivery {DeliveryId} with dispatcher {DispatcherId}, data type {DataType}, attempt {AttemptNumber}",
                    claim.DeliveryId,
                    dispatcherId,
                    GetDeliveryDataType(typeOfDispatcher, delivery.Data),
                    claim.AttemptNumber);

                dispatchResult = await dispatcher.DispatchAsync(delivery.Data, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Dispatch error for notification delivery {DeliveryId}, dispatcher {DispatcherId}, attempt {AttemptNumber}",
                    claim.DeliveryId,
                    dispatcherId,
                    claim.AttemptNumber);
                dispatchResult = new TaskResult();
                dispatchResult.AddError(
                    "DispatchError",
                    $"Failed to dispatch notification delivery {claim.DeliveryId} in channel {dispatcherId}: {ex.Message}");
            }

            try
            {
                await CompleteDeliveryAttemptAsync(
                    dispatcherId,
                    claim,
                    dispatchResult,
                    stoppingToken);
            }
            finally
            {
                inFlight.TryRemove(claim.DeliveryId, out _);
            }
        }
    }

    private static string GetDeliveryDataType(Type dispatcherType, object? deliveryData)
    {
        var typedDispatcher = dispatcherType
            .GetInterfaces()
            .FirstOrDefault(type => type.IsGenericType
                && type.GetGenericTypeDefinition() == typeof(INhNotificationDispatcher<>));

        return typedDispatcher?.GenericTypeArguments[0].FullName
            ?? deliveryData?.GetType().FullName
            ?? "null";
    }

    internal async Task CompleteDeliveryAttemptAsync(
        string dispatcherId,
        NotificationDeliveryClaim claim,
        TaskResult taskResult,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            using var scope = _scopeFactory.CreateScope();
            var repository = scope.ServiceProvider.GetRequiredService<IRepository<NhNotificationDelivery>>();
            await using var transactionScope = await repository.StartOrGetTransactionScopeAsync(cancellationToken);

            if (!await repository.TryAcquireTransactionLockAsync(
                    transactionScope,
                    GetPollerLockResourceName(),
                    (int)Math.Max(0, _settings.ProcessingPollerLockTimeout.TotalMilliseconds),
                    cancellationToken))
            {
                await Task.Delay(_idleDelay, cancellationToken);
                continue;
            }

            var delivery = await repository
                .GetAll()
                .Where(x => x.Notification!.ProcessorKey == _settings.ProcessorKey)
                .FirstOrDefaultAsync(x => x.Id == claim.DeliveryId, cancellationToken);

            if (delivery is null)
            {
                _logger.LogWarning(
                    "Notification delivery {DeliveryId} disappeared before attempt {AttemptNumber} could be completed by {DispatcherId}",
                    claim.DeliveryId,
                    claim.AttemptNumber,
                    dispatcherId);
                return;
            }

            if (delivery.Status != NotificationDeliveryStatus.Processing
                || delivery.AttemptCount != claim.AttemptNumber)
            {
                _logger.LogWarning(
                    "Ignoring stale result for notification delivery {DeliveryId}, dispatcher {DispatcherId}, attempt {AttemptNumber}; current status is {Status} and current attempt is {CurrentAttemptNumber}",
                    claim.DeliveryId,
                    dispatcherId,
                    claim.AttemptNumber,
                    delivery.Status,
                    delivery.AttemptCount);
                return;
            }

            if (taskResult.Success)
            {
                delivery.Status = NotificationDeliveryStatus.Succeeded;
                delivery.SentAt = DateTimeOffset.UtcNow;
            }
            else
            {
                _logger.LogError(
                    "Dispatch error for notification delivery {DeliveryId}, dispatcher {DispatcherId}, attempt {AttemptNumber}: [{Errors}]",
                    claim.DeliveryId,
                    dispatcherId,
                    claim.AttemptNumber,
                    string.Join("; ", taskResult.AllErrorMessages));

                if (delivery.AttemptCount >= Math.Max(1, _settings.ProcessingMaxRetryAttempts))
                {
                    delivery.Status = NotificationDeliveryStatus.Failed;
                    delivery.LastFailedMessage = "Max attempts reached with error: "
                        + (string.Join("; ", taskResult.AllErrorMessages) ?? "Unknown error");
                }
                else
                {
                    delivery.Status = NotificationDeliveryStatus.Queued;
                    delivery.ScheduledAt = DateTimeOffset.UtcNow.Add(_retryDelay);
                }
            }

            await repository.SaveChangesAsync(cancellationToken);
            await transactionScope.CommitAsync(cancellationToken);
            return;
        }
    }

    private async Task RunUnknownDispatcherLoopAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (_settings.ProcessingEnabled)
                {
                    await FailUnknownDispatcherDeliveriesAsync(stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error while failing notification deliveries without a registered dispatcher");
            }

            await Task.Delay(_idleDelay, stoppingToken);
        }
    }

    private async Task FailUnknownDispatcherDeliveriesAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IRepository<NhNotificationDelivery>>();
        await using var transactionScope = await repository.StartOrGetTransactionScopeAsync(cancellationToken);

        if (!await repository.TryAcquireTransactionLockAsync(
                transactionScope,
                GetPollerLockResourceName(),
                (int)Math.Max(0, _settings.ProcessingPollerLockTimeout.TotalMilliseconds),
                cancellationToken))
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var staleThreshold = now - _settings.ProcessingLockTimeout;
        var dispatcherIds = _dispatchers.Keys.ToList();
        var deliveries = await repository
            .GetAll()
            .Where(x => x.Notification!.ProcessorKey == _settings.ProcessorKey)
            .Where(x => !dispatcherIds.Contains(x.DispatcherId))
            .Where(x =>
                (x.Status == NotificationDeliveryStatus.Queued && x.ScheduledAt <= now)
                || (x.Status == NotificationDeliveryStatus.Processing
                    && (x.LastSendAttemptAt == null || x.LastSendAttemptAt <= staleThreshold)))
            .Take(100)
            .ToListAsync(cancellationToken);

        foreach (var delivery in deliveries)
        {
            delivery.Status = NotificationDeliveryStatus.Failed;
            delivery.LastSendAttemptAt = now;
            delivery.AttemptCount++;
            delivery.LastFailedMessage = $"No dispatcher for channel '{delivery.DispatcherId}'";
        }

        if (deliveries.Count > 0)
        {
            await repository.SaveChangesAsync(cancellationToken);
            await transactionScope.CommitAsync(cancellationToken);
        }
    }

    private async Task RunCleanupLoopAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Cleanup loop started, interval = {Interval}", _settings.ProcessingCleanupInterval);
        using var timer = new PeriodicTimer(_settings.ProcessingCleanupInterval);

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await CleanupOldSentItemsAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Exception during cleanup of old notifications");
            }
        }
    }

    private async Task CleanupOldSentItemsAsync(CancellationToken cancellationToken)
    {
        var threshold = DateTimeOffset.UtcNow - _settings.ProcessingRetentionPeriod;

        using var scope = _scopeFactory.CreateScope();
        var notificationRepository = scope.ServiceProvider.GetRequiredService<IRepository<NhNotificationDelivery>>();

        var oldDeliveries = await notificationRepository
            .GetAll()
            .Where(x => x.Notification!.ProcessorKey == _settings.ProcessorKey)
            .Where(d => !d.IsCleaned && d.Status == NotificationDeliveryStatus.Succeeded && d.SentAt <= threshold)
            .ToListAsync(cancellationToken);

        if (!oldDeliveries.Any())
        {
            _logger.LogInformation("No notifications found to cleanup.");
            return;
        }

        _logger.LogInformation("Cleanup: Found {Count} sent items older than {Threshold}",
            oldDeliveries.Count, threshold);

        foreach(var oldDelivery in oldDeliveries)
        {
            _logger.LogInformation("Cleaning up delivery {Id} with status {Status} and sent at {SentAt}",
                oldDelivery.Id, oldDelivery.Status, oldDelivery.SentAt);

            oldDelivery.Data = null;
            oldDelivery.IsCleaned = true;
        }

        await notificationRepository.SaveChangesAsync(cancellationToken);
        _logger.LogInformation("Cleanup completed: {Count} items cleaned up", oldDeliveries.Count);
    }
}
