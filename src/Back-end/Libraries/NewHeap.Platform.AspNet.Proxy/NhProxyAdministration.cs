using System.Collections.Immutable;
using Microsoft.AspNetCore.Http;
using NewHeap.Platform.Common.Attributes;
using NewHeap.Platform.Common.Models;

namespace NewHeap.Platform.AspNet.Proxy;

/// <summary>Credential input only. Never log or serialize this model into audit/configuration data.</summary>
public sealed class NhProxyLoginRequest
{
    public required string UserName { get; init; }
    public required string Password { get; init; }
}

/// <summary>Owns allowlist checks, throttling, credential validation, auditing, and cookie issuance.</summary>
public interface INhProxyAdministrationService
{
    /// <summary>A successful audit write must precede issuing a session. Audit failures cannot grant access.</summary>
    Task<TaskResult> SignInAsync(HttpContext context, NhProxyLoginRequest request, CancellationToken cancellationToken = default);
    Task SignOutAsync(HttpContext context, CancellationToken cancellationToken = default);

    /// <summary>IP access check for every administration endpoint, using only trusted forwarded addresses.</summary>
    Task<TaskResult> CheckIpAccessAsync(HttpContext context, CancellationToken cancellationToken = default);
}

public enum NhProxyLoginOutcome
{
    Success,
    InvalidCredentials,
    IpDenied,
    Throttled
}

/// <summary>UTC timestamp and normalized addresses; no arbitrary attempted username or secret fields.</summary>
public sealed record NhProxyLoginAuditEvent
{
    [Filterable]
    public required Guid Id { get; init; }
    public required DateTimeOffset OccurredAtUtc { get; init; }
    public string? ClientIp { get; init; }
    public string? ConnectionPeerIp { get; init; }
    public required NhProxyLoginOutcome Outcome { get; init; }
    public required string CorrelationId { get; init; }
    public string? AuthenticatedUserName { get; init; }
}

public sealed record NhProxyLoginAuditQuery
{
    public DateTimeOffset? FromUtc { get; init; }
    public DateTimeOffset? ToUtc { get; init; }
    public string? ClientIp { get; init; }
    public NhProxyLoginOutcome? Outcome { get; init; }
    public int Offset { get; init; }
    public int PageSize { get; init; } = 50;
}

/// <summary>Newest first, then stable event ID; filters and paging execute in storage.</summary>
public sealed record NhProxyLoginAuditPage(ImmutableArray<NhProxyLoginAuditEvent> Items, long TotalCount);

public interface INhProxyLoginAuditStore
{
    /// <summary>Append one event. Duplicate IDs must not create duplicate events; infrastructure errors throw.</summary>
    Task AppendAsync(NhProxyLoginAuditEvent auditEvent, CancellationToken cancellationToken = default);
    Task<TaskResult<NhProxyLoginAuditPage>> QueryAsync(NhProxyLoginAuditQuery query, CancellationToken cancellationToken = default);

    /// <summary>Internal retention workflow; never expose arbitrary deletion as an administration action.</summary>
    Task<int> DeleteExpiredAsync(DateTimeOffset olderThanUtc, int batchSize, CancellationToken cancellationToken = default);
}
