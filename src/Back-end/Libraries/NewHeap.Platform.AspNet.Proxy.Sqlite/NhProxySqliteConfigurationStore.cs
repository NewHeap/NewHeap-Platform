using Microsoft.Extensions.Options;
using NewHeap.Platform.Common.Models;

namespace NewHeap.Platform.AspNet.Proxy.Sqlite;

/// <summary>SQLite contract stub. Initialization, ownership, upgrades, reads, and writes are not implemented.</summary>
public sealed class NhProxySqliteConfigurationStore : INhProxyConfigurationStore
{
    public NhProxySqliteConfigurationStore(IOptions<NhProxySqliteOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
    }

    public Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public Task<NhProxyRewriteConfiguration> LoadRewritesAsync(CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public Task<NhProxyRedirectConfiguration> LoadRedirectsAsync(CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public Task<TaskResult<NhProxyRewriteConfiguration>> SaveRewritesAsync(NhProxyRewriteSaveRequest request, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public Task<TaskResult<NhProxyRedirectConfiguration>> SaveRedirectsAsync(NhProxyRedirectSaveRequest request, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public ValueTask DisposeAsync()
    {
        throw new NotImplementedException();
    }
}
