using Microsoft.Extensions.Hosting;

namespace NewHeap.Platform.AspNet.Proxy.Sqlite;

internal sealed class NhProxyInitializationService(INhProxyConfigurationService configuration, INhProxyAdministrationService administration) : IHostedLifecycleService
{
    public async Task StartingAsync(CancellationToken cancellationToken)
    {
        // Resolve and validate host-owned security settings before accepting requests.
        _ = administration;
        await configuration.InitializeAsync(cancellationToken);
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
