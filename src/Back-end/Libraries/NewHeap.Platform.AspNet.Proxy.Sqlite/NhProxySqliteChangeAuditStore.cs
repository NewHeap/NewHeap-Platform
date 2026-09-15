using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using NewHeap.Platform.Common.Models;

namespace NewHeap.Platform.AspNet.Proxy.Sqlite;

/// <summary>Append-only API change audit, retained until explicitly managed by the operator.</summary>
public sealed class NhProxySqliteChangeAuditStore(IOptions<NhProxySqliteOptions> options, IWebHostEnvironment? environment = null)
    : INhProxyChangeAuditStore
{
    private readonly string _connectionString = new NhProxySqliteConfigurationStore(options, environment).ConnectionString;

    public async Task<TaskResult<NhProxyChangeAuditPage>> QueryAsync(NhProxyChangeAuditQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.Offset < 0 || query.PageSize is < 1 or > 100 || query.FromUtc > query.ToUtc
            || (query.Engine is { } engine && !Enum.IsDefined(engine)))
        {
            return TaskResult<NhProxyChangeAuditPage>.Failed(NhProxyErrorCodes.Validation, "newheap-proxy.invalid-change-audit-query");
        }

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        using var transaction = connection.BeginTransaction(deferred: true);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        const string filter = "WHERE ($engine IS NULL OR Engine=$engine) AND ($from IS NULL OR OccurredAtUtc >= $from) AND ($to IS NULL OR OccurredAtUtc <= $to)";
        command.Parameters.AddWithValue("$engine", query.Engine is { } selected ? (int)selected : DBNull.Value);
        command.Parameters.AddWithValue("$from", query.FromUtc is { } from ? from.ToUnixTimeMilliseconds() : DBNull.Value);
        command.Parameters.AddWithValue("$to", query.ToUtc is { } to ? to.ToUnixTimeMilliseconds() : DBNull.Value);
        command.CommandText = "SELECT COUNT(*) FROM NhProxyChangeAudit " + filter;
        var count = (long)(await command.ExecuteScalarAsync(cancellationToken))!;
        command.CommandText = """
            SELECT Id, OccurredAtUtc, UserName, ClientIp, CorrelationId, Engine, Action,
                   PreviousRevision, Revision, BeforeJson, AfterJson
            FROM NhProxyChangeAudit
            """ + " " + filter + " ORDER BY OccurredAtUtc DESC, rowid DESC LIMIT $limit OFFSET $offset;";
        command.Parameters.AddWithValue("$limit", query.PageSize);
        command.Parameters.AddWithValue("$offset", query.Offset);
        var items = ImmutableArray.CreateBuilder<NhProxyChangeAuditEvent>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(new NhProxyChangeAuditEvent
            {
                Id = Guid.Parse(reader.GetString(0)), OccurredAtUtc = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(1)),
                Actor = new(reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetString(4)),
                Engine = (NhProxyEngine)reader.GetInt32(5), Action = (NhProxyChangeAuditAction)reader.GetInt32(6),
                PreviousRevision = reader.IsDBNull(7) ? null : reader.GetInt64(7), Revision = reader.IsDBNull(8) ? null : reader.GetInt64(8),
                Before = reader.IsDBNull(9) ? null : JsonSerializer.Deserialize<JsonElement>(reader.GetString(9)),
                After = reader.IsDBNull(10) ? null : JsonSerializer.Deserialize<JsonElement>(reader.GetString(10))
            });
        }

        return TaskResult<NhProxyChangeAuditPage>.Succeeded(new(items.ToImmutable(), count));
    }

    public async Task RecordActivationRequestAsync(NhProxyEngine engine, NhProxyChangeAuditContext actor, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (!Enum.IsDefined(engine))
        {
            throw new ArgumentOutOfRangeException(nameof(engine));
        }

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await AppendAsync(connection, null, new NhProxyChangeAuditEvent
        {
            Id = Guid.NewGuid(), OccurredAtUtc = DateTimeOffset.UtcNow, Actor = actor, Engine = engine,
            Action = NhProxyChangeAuditAction.ActivationRequested
        }, cancellationToken);
    }

    internal static async Task AppendAsync(SqliteConnection connection, SqliteTransaction? transaction, NhProxyChangeAuditEvent entry, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entry.Actor.UserName);
        ArgumentException.ThrowIfNullOrWhiteSpace(entry.Actor.CorrelationId);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO NhProxyChangeAudit
                (Id, OccurredAtUtc, UserName, ClientIp, CorrelationId, Engine, Action, PreviousRevision, Revision, BeforeJson, AfterJson)
            VALUES ($id, $time, $user, $ip, $correlation, $engine, $action, $previous, $revision, $before, $after);
            """;
        command.Parameters.AddWithValue("$id", entry.Id.ToString("D"));
        command.Parameters.AddWithValue("$time", entry.OccurredAtUtc.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$user", entry.Actor.UserName);
        command.Parameters.AddWithValue("$ip", (object?)entry.Actor.ClientIp ?? DBNull.Value);
        command.Parameters.AddWithValue("$correlation", entry.Actor.CorrelationId);
        command.Parameters.AddWithValue("$engine", (int)entry.Engine);
        command.Parameters.AddWithValue("$action", (int)entry.Action);
        command.Parameters.AddWithValue("$previous", (object?)entry.PreviousRevision ?? DBNull.Value);
        command.Parameters.AddWithValue("$revision", (object?)entry.Revision ?? DBNull.Value);
        command.Parameters.AddWithValue("$before", (object?)entry.Before?.GetRawText() ?? DBNull.Value);
        command.Parameters.AddWithValue("$after", (object?)entry.After?.GetRawText() ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
