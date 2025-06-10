using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NewHeap.Platform.AspNet.Common.DAL.Entities;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace NewHeap.Platform.AspNet.Common.Services.Notification;
internal class NhNotificationProcessingService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IDictionary<string, Channel<NhNotificationDelivery>> _queues
        = new Dictionary<string, Channel<NhNotificationDelivery>>();
    private readonly IDictionary<string, INhNotificationDispatcher> _dispatchers
        = new Dictionary<string, INhNotificationDispatcher>();
    private readonly ILogger<NhNotificationProcessingService> _logger;

    public NhNotificationProcessingService(
        IServiceScopeFactory scopeFactory,
        IEnumerable<INhNotificationDispatcher> dispatchers,
        ILogger<NhNotificationProcessingService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;

        foreach (var disp in dispatchers)
        {
            _dispatchers[disp.DispatcherId] = disp;
            _queues[disp.DispatcherId] = Channel.CreateUnbounded<NhNotificationDelivery>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var consumerTasks = _dispatchers
            .Select(kv => RunChannelConsumerAsync(kv.Key, kv.Value, _queues[kv.Key], stoppingToken))
            .ToArray();

        // 2) Start de hoofd-pollingloop die DB regelmatig nakijkt en items enqueue’t:
        var producerTask = RunDatabasePollerAsync(stoppingToken);

        // Wacht tot shutdown
        await Task.WhenAll(consumerTasks.Concat(new[] { producerTask }));
    }

    private async Task RunDatabasePollerAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Notification poller gestart");
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<Ire>();

                var now = DateTimeOffset.UtcNow;
                // Haal max. 100 items per run om batches in te stellen
                var pending = await db.NotificationDeliveries
                    .Where(d => d.Status == "Pending" && d.ScheduledAt <= now)
                    .OrderBy(d => d.ScheduledAt)
                    .Take(100)
                    .ToListAsync(stoppingToken);

                foreach (var delivery in pending)
                {
                    // Markeer direct als 'Processing' om dubbele picks te voorkomen
                    delivery.Status = "Processing";
                    delivery.LastTriedAt = now;
                }
                await db.SaveChangesAsync(stoppingToken);

                // Enqueue
                foreach (var delivery in pending)
                {
                    if (_queues.TryGetValue(delivery.ChannelType, out var queue))
                    {
                        await queue.Writer.WriteAsync(delivery, stoppingToken);
                    }
                    else
                    {
                        // Onbekend kanaal → meteen faalbericht en sla op
                        delivery.Status = "Failed";
                        delivery.ErrorMessage = $"No dispatcher for channel '{delivery.ChannelType}'";
                        db.Update(delivery);
                        await db.SaveChangesAsync(stoppingToken);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Fout tijdens polling van notificaties");
            }

            // Pauze, bv. elke 5s
            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        }
    }

    private async Task RunChannelConsumerAsync(
        string channelType,
        INhNotificationDispatcher dispatcher,
        Channel<NhNotificationDelivery> queue,
        CancellationToken stoppingToken)
    {
        _logger.LogInformation("Consumer voor kanaal {Channel} gestart", channelType);

        await foreach (var delivery in queue.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                // Despatch
                var externalId = await dispatcher.DispatchAsync(delivery, stoppingToken);

                // Update DB na succes
                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<NotificationDbContext>();
                delivery.Status = "Sent";
                delivery.SentAt = DateTimeOffset.UtcNow;
                delivery.ExternalId = externalId;
                db.Update(delivery);
                await db.SaveChangesAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Dispatch fout in kanaal {Channel}", channelType);

                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<NotificationDbContext>();
                delivery.AttemptCount++;
                delivery.ErrorMessage = ex.Message;
                if (delivery.AttemptCount >= 3)
                {
                    delivery.Status = "Failed";
                }
                else
                {
                    delivery.Status = "RetryScheduled";
                    delivery.ScheduledAt = DateTimeOffset.UtcNow.AddMinutes(5);
                }
                db.Update(delivery);
                await db.SaveChangesAsync(stoppingToken);
            }
        }

        _logger.LogInformation("Consumer voor kanaal {Channel} beëindigd", channelType);
    }
}