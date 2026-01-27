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
using System.Reflection;
using System.Text;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace NewHeap.Platform.AspNet.Common.Services.Notification;

public class NhNotificationSettings
{
    public string ProcessorKey { get; set; } = "default";
    public bool ProcessingEnabled { get; set; } = true;
    public TimeSpan ProcessingLockTimeout { get;set; } = TimeSpan.FromMinutes(1);
    public int ProcessingMaxRetryAttempts { get; set; } = 3;
    public TimeSpan ProcessingCleanupInterval { get; set; } = TimeSpan.FromHours(1);
    public TimeSpan ProcessingRetentionPeriod { get; set; } = TimeSpan.FromDays(30);

}

internal class NhNotificationProcessingService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IDictionary<string, Channel<Guid>> _queues
        = new Dictionary<string, Channel<Guid>>();
    private readonly IDictionary<string, Type> _dispatchers
        = new Dictionary<string, Type>();
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<Guid, byte>> _inFlight
        = new ConcurrentDictionary<string, ConcurrentDictionary<Guid, byte>>();
    private readonly ILogger<NhNotificationProcessingService> _logger;
    private NhNotificationSettings _settings;

    public NhNotificationProcessingService(
        IServiceScopeFactory scopeFactory,
        ILogger<NhNotificationProcessingService> logger,
        IOptionsMonitor<NhNotificationSettings> settingsOptionsMonitor
        )
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
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
                .Select(kv => RunChannelConsumerAsync(kv.Key, kv.Value, _queues[kv.Key], stoppingToken))
                .ToArray();

            // Start cleanup-task parallel
            var cleanupTask = RunCleanupLoopAsync(stoppingToken);

            var producerTask = RunDatabasePollerAsync(stoppingToken);

            await Task.WhenAll(processingTasks.Concat(new[] { producerTask }));
        }
        catch(Exception ex)
        {
            _logger.LogError(ex, "Error in NhNotificationProcessingService");
            ;
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
            _queues[disp.DispatcherId] = Channel.CreateUnbounded<Guid>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
            _inFlight[disp.DispatcherId] = new ConcurrentDictionary<Guid, byte>();
        }
    }

    private async Task RunDatabasePollerAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Notification poller started");
        const int taskDelayTimeInSec = 5;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!_settings.ProcessingEnabled)
                { 
                    await Task.Delay(TimeSpan.FromSeconds(taskDelayTimeInSec), stoppingToken);
                    continue;
                }

                using var scope = _scopeFactory.CreateScope();
                var notificationDeliveryRepository = scope.ServiceProvider.GetRequiredService<IRepository<NhNotificationDelivery>>();

                var now = DateTimeOffset.UtcNow;
                var staleThreshold = now - _settings.ProcessingLockTimeout;

                var inFlightGuids = _inFlight
                    .SelectMany(kv => kv.Value.Keys)
                    .ToList();

                var candidates = await notificationDeliveryRepository
                    .GetAll()
                    .Where(x => x.Notification!.ProcessorKey == _settings.ProcessorKey)
                    .Where(d => 
                        (
                            (d.Status == NotificationDeliveryStatus.Queued && d.ScheduledAt <= now)
                            || (d.Status == NotificationDeliveryStatus.Processing && !inFlightGuids.Contains(d.Id)) // Pick up hanging, we skip que if they still in processing
                        )
                    )
                    .Select(x => new { Priority = (x.Priority.HasValue ? x.Priority : x.Notification!.Priority), NotificationDelivery = x })
                    .OrderByDescending(d => d.Priority)
                        .ThenBy(d => d.NotificationDelivery.ScheduledAt)
                    .Take(100)
                    .Select(d => d.NotificationDelivery)
                    .ToListAsync(stoppingToken);

                foreach (var delivery in candidates)
                {
                    delivery.Status = NotificationDeliveryStatus.Processing;
                }

                await notificationDeliveryRepository.SaveChangesAsync(stoppingToken);

                foreach (var delivery in candidates)
                {
                    if (!_queues.TryGetValue(delivery.DispatcherId, out var queue))
                    {
                        delivery.Status = NotificationDeliveryStatus.Failed;
                        delivery.LastSendAttemptAt = DateTimeOffset.UtcNow;
                        delivery.AttemptCount++;
                        delivery.LastFailedMessage = $"No dispatcher for channel '{delivery.DispatcherId}'";

                        await notificationDeliveryRepository.SaveChangesAsync(stoppingToken);
                        continue;
                    }

                    var inFlightSet = _inFlight[delivery.DispatcherId];

                    if (inFlightSet.TryAdd(delivery.Id, 0))
                    {
                        try
                        {
                            await queue.Writer.WriteAsync(delivery.Id, stoppingToken);
                        }
                        catch(Exception ex)
                        {
                            _logger.LogError(ex, "Exception occured during polling of notifications inFlightSet");
                            inFlightSet.TryRemove(delivery.Id, out _);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Exception occured during polling of notifications");
            }

            await Task.Delay(TimeSpan.FromSeconds(taskDelayTimeInSec), stoppingToken);
        }
    }

    private async Task RunChannelConsumerAsync(
        string dispatcherId,
        Type typeOfDispatcher,
        Channel<Guid> queue,
        CancellationToken stoppingToken)
    {
        _logger.LogInformation("Notificaiton consumer for channel {Channel} started", dispatcherId);

        await foreach (var deliveryId in queue.Reader.ReadAllAsync(stoppingToken))
        {
            using var scope = _scopeFactory.CreateScope();
            var notificationRepository = scope.ServiceProvider.GetRequiredService<IRepository<NhNotificationDelivery>>();
            var dispatcher = scope
                .ServiceProvider
                .GetServices<INhNotificationDispatcher>()
                .Where(x => x.GetType() == typeOfDispatcher)
                .First();

            var delivery = await notificationRepository
                .GetAll()
                .Where(x => x.Notification!.ProcessorKey == _settings.ProcessorKey)
                .FirstOrDefaultAsync(d => d.Id == deliveryId, stoppingToken);

            if (delivery == null)
            {
                return;
            }

            try
            {
                var dispatchResult = await dispatcher.DispatchAsync(delivery.Data, stoppingToken);
                await handleResult(dispatchResult);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Dispatch error in channel {Channel}", dispatcherId);
                var errorResult = new TaskResult();
                errorResult.AddError("DispatchError", $"Failed to dispatch notification delivery {deliveryId} in channel {dispatcherId}: {ex.Message}");

                await handleResult(errorResult);
            }
            finally
            {
                _inFlight[dispatcherId].TryRemove(deliveryId, out _);
            }

            continue;

            async Task handleResult(TaskResult taskResult)
            {
                delivery.AttemptCount++;
                delivery.LastSendAttemptAt = DateTimeOffset.UtcNow;

                if (taskResult.Success)
                {
                    delivery.Status = NotificationDeliveryStatus.Succeeded;
                    delivery.SentAt = DateTimeOffset.UtcNow;
                }
                else
                {
                    _logger.LogError($"Dispatch error in channel {dispatcherId}: [{string.Join("; ", taskResult.AllErrorMessages)}]");

                    if (delivery.AttemptCount >= _settings.ProcessingMaxRetryAttempts)
                    {
                        delivery.Status = NotificationDeliveryStatus.Failed;
                        delivery.LastFailedMessage = "Max attempts reached with error: " + string.Join("; ", taskResult.AllErrorMessages) ?? "Unknown error";
                    }
                    else
                    {
                        delivery.Status = NotificationDeliveryStatus.Queued;
                        delivery.ScheduledAt = DateTimeOffset.UtcNow.AddSeconds(5);
                    }
                }

                await notificationRepository.SaveChangesAsync(stoppingToken);
            }
        }

        _logger.LogInformation("Consumer for channel {Channel} stopped", dispatcherId);
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