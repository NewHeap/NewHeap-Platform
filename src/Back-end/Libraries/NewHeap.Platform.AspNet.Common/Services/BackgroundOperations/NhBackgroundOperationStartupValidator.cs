using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NewHeap.Platform.AspNet.Common.DAL;
using NewHeap.Platform.AspNet.Common.DAL.Entities;
using NewHeap.Platform.AspNet.Common.Services.Notification;

namespace NewHeap.Platform.AspNet.Common.Services.BackgroundOperations;

internal sealed class NhBackgroundOperationStartupValidator : IHostedService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly NhBackgroundOperationsOptions _options;
    private readonly ILogger<NhBackgroundOperationStartupValidator> _logger;

    public NhBackgroundOperationStartupValidator(
        IServiceProvider serviceProvider,
        NhBackgroundOperationsOptions options,
        ILogger<NhBackgroundOperationStartupValidator> logger)
    {
        _serviceProvider = serviceProvider;
        _options = options;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _options.Validate();
        using var scope = _serviceProvider.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IRepository<NhBackgroundOperation>>();
        if (repository.Context.Model.FindEntityType(typeof(NhBackgroundOperation)) is null
            || repository.Context.Model.FindEntityType(typeof(NhBackgroundOperationLease)) is null)
        {
            throw new InvalidOperationException("The configured EF Core context does not include the NewHeap background-operation model.");
        }
        try
        {
            // Validate the deployed schema, not only the in-memory EF model. A
            // consumer migration must be deployed before workers are enabled.
            await repository.GetDbSet<NhBackgroundOperation>()
                .AsNoTracking().Select(x => x.Id).Take(1).ToListAsync(cancellationToken);
            await repository.GetDbSet<NhBackgroundOperationAttempt>()
                .AsNoTracking().Select(x => x.Id).Take(1).ToListAsync(cancellationToken);
            await repository.GetDbSet<NhBackgroundOperationStep>()
                .AsNoTracking().Select(x => x.Id).Take(1).ToListAsync(cancellationToken);
            await repository.GetDbSet<NhBackgroundOperationEvent>()
                .AsNoTracking().Select(x => x.Id).Take(1).ToListAsync(cancellationToken);
            await repository.GetDbSet<NhBackgroundOperationCheckpoint>()
                .AsNoTracking().Select(x => x.OperationId).Take(1).ToListAsync(cancellationToken);
            await repository.GetDbSet<NhBackgroundOperationIdempotencyRecord>()
                .AsNoTracking().Select(x => x.OperationId).Take(1).ToListAsync(cancellationToken);
            await repository.GetDbSet<NhBackgroundOperationLease>()
                .AsNoTracking().Select(x => x.ResourceKey).Take(1).ToListAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new InvalidOperationException(
                "The background-operation database schema is unavailable. Generate and deploy the consumer DbContext migration before enabling background operations.",
                exception);
        }
        if (_options.DispatchWorkersEnabled
            && scope.ServiceProvider.GetService<IBackgroundJobClient>() is null)
        {
            throw new InvalidOperationException("Background-operation dispatch is enabled, but Hangfire is not configured. Call WithHangfire or disable DispatchWorkersEnabled.");
        }

        if (_options.UserNotificationProjectionEnabled
            && scope.ServiceProvider.GetService<INhUserNotificationService>() is null)
        {
            throw new InvalidOperationException("Background-operation notification projection is enabled, but user notifications are not configured. Call WithNotifications or disable UserNotificationProjectionEnabled.");
        }

        _logger.LogInformation(
            "Background operation infrastructure validated for processor {ProcessorKey}. Dispatch workers: {DispatchWorkersEnabled}; live updates: {LiveUpdatesEnabled}; notification projection: {UserNotificationProjectionEnabled}.",
            _options.ProcessorKey,
            _options.DispatchWorkersEnabled,
            _options.LiveUpdatesEnabled,
            _options.UserNotificationProjectionEnabled);
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}
