using System.Collections.Immutable;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using NewHeap.Platform.Common.Models;

namespace NewHeap.Platform.AspNet.Proxy.Sqlite;

/// <summary>Persistent, paged login audit. Configuration storage owns schema initialization.</summary>
public sealed class NhProxySqliteLoginAuditStore : INhProxyLoginAuditStore
{
    private readonly string _connectionString;
    private readonly int _maximumPageSize;

    public NhProxySqliteLoginAuditStore(IOptions<NhProxySqliteOptions> options,
        IWebHostEnvironment? environment = null, IOptions<NhProxyOptions>? proxyOptions = null)
    {
        _connectionString = new NhProxySqliteConfigurationStore(options, environment).ConnectionString;
        _maximumPageSize = proxyOptions?.Value.LoginAudit.MaximumPageSize ?? 100;
    }

    public async Task AppendAsync(NhProxyLoginAuditEvent auditEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(auditEvent);
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO NhProxyLoginAudit (Id, OccurredAtUtc, ClientIp, ConnectionPeerIp, Outcome, CorrelationId, AuthenticatedUserName)
            VALUES ($id, $time, $ip, $peer, $outcome, $correlation, $user) ON CONFLICT(Id) DO NOTHING;
            """;
        command.Parameters.AddWithValue("$id", auditEvent.Id.ToString("D"));
        command.Parameters.AddWithValue("$time", auditEvent.OccurredAtUtc.UtcTicks);
        command.Parameters.AddWithValue("$ip", (object?)auditEvent.ClientIp ?? DBNull.Value);
        command.Parameters.AddWithValue("$peer", (object?)auditEvent.ConnectionPeerIp ?? DBNull.Value);
        command.Parameters.AddWithValue("$outcome", (int)auditEvent.Outcome);
        command.Parameters.AddWithValue("$correlation", auditEvent.CorrelationId);
        command.Parameters.AddWithValue("$user", (object?)auditEvent.AuthenticatedUserName ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<TaskResult<NhProxyLoginAuditPage>> QueryAsync(NhProxyLoginAuditQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.Offset < 0 || query.PageSize < 1 || query.PageSize > _maximumPageSize
            || query.FromUtc > query.ToUtc || (query.Outcome is not null && !Enum.IsDefined(query.Outcome.Value)))
        {
            return TaskResult<NhProxyLoginAuditPage>.Failed(NhProxyErrorCodes.Validation, NhProxyErrorCodes.Validation);
        }

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        const string filter = "WHERE ($from IS NULL OR OccurredAtUtc >= $from) AND ($to IS NULL OR OccurredAtUtc <= $to) AND ($ip IS NULL OR ClientIp = $ip) AND ($outcome IS NULL OR Outcome = $outcome)";
        command.Parameters.AddWithValue("$from", (object?)query.FromUtc?.UtcTicks ?? DBNull.Value);
        command.Parameters.AddWithValue("$to", (object?)query.ToUtc?.UtcTicks ?? DBNull.Value);
        command.Parameters.AddWithValue("$ip", (object?)query.ClientIp ?? DBNull.Value);
        command.Parameters.AddWithValue("$outcome", query.Outcome is null ? DBNull.Value : (object)(int)query.Outcome.Value);
        command.CommandText = "SELECT COUNT(*) FROM NhProxyLoginAudit " + filter;
        var count = (long)(await command.ExecuteScalarAsync(cancellationToken))!;
        command.CommandText = "SELECT Id, OccurredAtUtc, ClientIp, ConnectionPeerIp, Outcome, CorrelationId, AuthenticatedUserName FROM NhProxyLoginAudit " + filter + " ORDER BY OccurredAtUtc DESC, Id LIMIT $limit OFFSET $offset;";
        command.Parameters.AddWithValue("$limit", query.PageSize);
        command.Parameters.AddWithValue("$offset", query.Offset);
        var items = ImmutableArray.CreateBuilder<NhProxyLoginAuditEvent>();
        using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                items.Add(new NhProxyLoginAuditEvent
                {
                    Id = Guid.Parse(reader.GetString(0)), OccurredAtUtc = new DateTimeOffset(reader.GetInt64(1), TimeSpan.Zero),
                    ClientIp = reader.IsDBNull(2) ? null : reader.GetString(2),
                    ConnectionPeerIp = reader.IsDBNull(3) ? null : reader.GetString(3),
                    Outcome = (NhProxyLoginOutcome)reader.GetInt32(4), CorrelationId = reader.GetString(5),
                    AuthenticatedUserName = reader.IsDBNull(6) ? null : reader.GetString(6)
                });
            }
        }

        await transaction.CommitAsync(cancellationToken);
        return TaskResult<NhProxyLoginAuditPage>.Succeeded(new(items.ToImmutable(), count));
    }

    public async Task<int> DeleteExpiredAsync(DateTimeOffset olderThanUtc, int batchSize, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batchSize);
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM NhProxyLoginAudit WHERE Id IN (SELECT Id FROM NhProxyLoginAudit WHERE OccurredAtUtc < $cutoff ORDER BY OccurredAtUtc, Id LIMIT $limit);";
        command.Parameters.AddWithValue("$cutoff", olderThanUtc.UtcTicks);
        command.Parameters.AddWithValue("$limit", batchSize);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
