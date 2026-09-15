using System.Collections.Immutable;
using System.Text.Json;
using NewHeap.Platform.Common.Attributes;
using NewHeap.Platform.Common.Models;

namespace NewHeap.Platform.AspNet.Proxy;

/// <summary>Server-supplied attribution, never accepted from an API request body.</summary>
public sealed record NhProxyChangeAuditContext(string UserName, string? ClientIp, string CorrelationId);

public enum NhProxyChangeAuditAction
{
    ConfigurationSaved,
    ActivationRequested
}

/// <summary>
/// ConfigurationSaved records the committed before/after snapshots, independently of later activation.
/// ActivationRequested records intent before a retry; it does not assert that activation succeeded.
/// Credentials, authentication headers and raw HTTP requests are never included.
/// </summary>
public sealed record NhProxyChangeAuditEvent
{
    [Filterable]
    public required Guid Id { get; init; }
    public required DateTimeOffset OccurredAtUtc { get; init; }
    public required NhProxyChangeAuditContext Actor { get; init; }
    public required NhProxyEngine Engine { get; init; }
    public required NhProxyChangeAuditAction Action { get; init; }
    public long? PreviousRevision { get; init; }
    public long? Revision { get; init; }
    public JsonElement? Before { get; init; }
    public JsonElement? After { get; init; }
}

public sealed record NhProxyChangeAuditQuery
{
    public NhProxyEngine? Engine { get; init; }
    public DateTimeOffset? FromUtc { get; init; }
    public DateTimeOffset? ToUtc { get; init; }
    public int Offset { get; init; }
    public int PageSize { get; init; } = 50;
}

public sealed record NhProxyChangeAuditPage(ImmutableArray<NhProxyChangeAuditEvent> Items, long TotalCount);

/// <summary>Persistent audit access. Configuration stores must write save events in the same transaction as the configuration.</summary>
public interface INhProxyChangeAuditStore
{
    Task<TaskResult<NhProxyChangeAuditPage>> QueryAsync(NhProxyChangeAuditQuery query, CancellationToken cancellationToken = default);
    Task RecordActivationRequestAsync(NhProxyEngine engine, NhProxyChangeAuditContext actor, CancellationToken cancellationToken = default);
}
