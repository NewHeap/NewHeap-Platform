using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.Common.Models;
using Yarp.ReverseProxy;
using Yarp.ReverseProxy.Configuration;

namespace NewHeap.Platform.AspNet.Proxy;

internal sealed class NhProxyRewriteRuntime(INhProxyConfigurationValidator validator, InMemoryConfigProvider provider,
    IServiceProvider services) : IConfigChangeListener
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private NhProxyEngineStatus _status = new(NhProxyEngine.Rewrite, 0, null, NhProxyActivationState.NotInitialized);
    private sealed record Publication(long Revision, string Token, TaskCompletionSource<bool> Completion);
    private Publication? _publication;
    private HashSet<string>? _destinationOrigins;

    internal NhProxyEngineStatus Status => Volatile.Read(ref _status);

    internal async Task<TaskResult<NhProxyEngineStatus>> PublishAsync(NhProxyRewriteConfiguration configuration, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (configuration.FormatVersion != 1)
        {
            return TaskResult<NhProxyEngineStatus>.Failed(NhProxyErrorCodes.Validation, "newheap-proxy.unsupported-format");
        }

        var validation = await validator.ValidateRewritesAsync(new(configuration.Revision, configuration.Rules, configuration.Clusters), cancellationToken);
        if (!validation.Success)
        {
            return TaskResult<NhProxyEngineStatus>.Failed(validation);
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (Status.ActiveRevision >= configuration.Revision || Status.DesiredRevision > configuration.Revision)
            {
                return TaskResult<NhProxyEngineStatus>.Failed(NhProxyErrorCodes.RevisionConflict, NhProxyErrorCodes.RevisionConflict);
            }

            var publication = new Publication(configuration.Revision, Guid.NewGuid().ToString("N"), new(TaskCreationOptions.RunContinuationsAsynchronously));
            Volatile.Write(ref _publication, publication);
            Volatile.Write(ref _status, Status with { DesiredRevision = configuration.Revision, State = NhProxyActivationState.Pending });
            provider.Update(configuration.Rules.Where(rule => rule.Enabled).Select(NhProxyYarpConfiguration.Route).ToArray(),
                configuration.Clusters.Select(NhProxyYarpConfiguration.Cluster).ToArray(), publication.Token);

            // Materialize mapped endpoints during StartingAsync, before the HTTP server starts.
            _ = services.GetRequiredService<EndpointDataSource>().Endpoints;
            try
            {
                var applied = await publication.Completion.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
                return applied ? TaskResult<NhProxyEngineStatus>.Succeeded(Status)
                    : TaskResult<NhProxyEngineStatus>.Failed(NhProxyErrorCodes.ActivationFailed, NhProxyErrorCodes.ActivationFailed);
            }
            catch (TimeoutException)
            {
                // Update is only a notification; a late listener callback can still confirm this revision.
                var current = Status;
                if (current.State == NhProxyActivationState.Pending)
                {
                    Interlocked.CompareExchange(ref _status, current with { State = NhProxyActivationState.Unconfirmed }, current);
                }
                return TaskResult<NhProxyEngineStatus>.Failed(NhProxyErrorCodes.ActivationFailed, "newheap-proxy.activation-unconfirmed");
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    internal bool HasOnlyExternalDestinations(Uri origin)
    {
        var origins = Volatile.Read(ref _destinationOrigins);
        return origins is not null && !origins.Contains(OriginKey(origin));
    }

    private static string OriginKey(Uri uri) => $"{uri.Scheme}://{uri.IdnHost}:{uri.Port}";

    public void ConfigurationApplied(IReadOnlyList<IProxyConfig> proxyConfigs)
    {
        // Read applied state: host filters can change destinations after loading the source configuration.
        var origins = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var cluster in services.GetRequiredService<IProxyStateLookup>().GetClusters())
        {
            foreach (var destination in cluster.Model.Config.Destinations?.Values ?? [])
            {
                if (!Uri.TryCreate(destination.Address, UriKind.Absolute, out var address))
                {
                    // Unknown destinations must retain the full chain check.
                    Volatile.Write(ref _destinationOrigins, null);
                    Complete(proxyConfigs, true);
                    return;
                }

                origins.Add(OriginKey(address));
            }
        }

        Volatile.Write(ref _destinationOrigins, origins);
        Complete(proxyConfigs, true);
    }

    public void ConfigurationApplyingFailed(IReadOnlyList<IProxyConfig> proxyConfigs, Exception exception)
    {
        Volatile.Write(ref _destinationOrigins, null);
        Complete(proxyConfigs, false);
    }

    public void ConfigurationLoadingFailed(IProxyConfigProvider configProvider, Exception exception) { }
    public void ConfigurationLoaded(IReadOnlyList<IProxyConfig> proxyConfigs)
    {
        // Disable the shortcut before YARP mutates active clusters; re-enable only after successful application.
        Volatile.Write(ref _destinationOrigins, null);
    }

    private void Complete(IReadOnlyList<IProxyConfig> configurations, bool success)
    {
        var publication = Volatile.Read(ref _publication);
        if (publication is null || !configurations.Any(config => config.RevisionId == publication.Token))
        {
            return;
        }

        Volatile.Write(ref _status, new(NhProxyEngine.Rewrite, publication.Revision,
            success ? publication.Revision : Status.ActiveRevision, success ? NhProxyActivationState.Active : NhProxyActivationState.Failed));
        publication.Completion.TrySetResult(success);
    }
}
