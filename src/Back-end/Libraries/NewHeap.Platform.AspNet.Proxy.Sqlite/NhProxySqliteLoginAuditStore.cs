using Microsoft.Extensions.Options;
using NewHeap.Platform.Common.Models;

namespace NewHeap.Platform.AspNet.Proxy.Sqlite;

/// <summary>SQLite audit contract stub. No event is persisted or reported as persisted yet.</summary>
public sealed class NhProxySqliteLoginAuditStore : INhProxyLoginAuditStore
{
    public NhProxySqliteLoginAuditStore(IOptions<NhProxySqliteOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
    }

    public Task AppendAsync(NhProxyLoginAuditEvent auditEvent, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public Task<TaskResult<NhProxyLoginAuditPage>> QueryAsync(NhProxyLoginAuditQuery query, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public Task<int> DeleteExpiredAsync(DateTimeOffset olderThanUtc, int batchSize, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }
}
