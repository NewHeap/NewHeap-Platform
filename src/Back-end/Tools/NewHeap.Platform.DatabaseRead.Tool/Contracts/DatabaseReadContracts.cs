using System.Text.Json;
using System.Text.Json.Serialization;

namespace NewHeap.Platform.DatabaseRead;

internal sealed class DatabaseReadRequest
{
    public int SchemaVersion { get; init; }

    public string? Profile { get; init; }

    public string? Sql { get; init; }

    public IReadOnlyList<DatabaseReadParameterRequest>? Parameters { get; init; }

    public DatabaseReadLimitRequest? Limits { get; init; }

    public string? Reason { get; init; }
}

internal sealed class DatabaseReadParameterRequest
{
    public string? Name { get; init; }

    public string? Type { get; init; }

    [JsonRequired]
    public JsonElement Value { get; init; }
}

internal sealed class DatabaseReadLimitRequest
{
    public int? MaximumRows { get; init; }

    public int? TimeoutSeconds { get; init; }
}

internal sealed class DatabaseReadProfileCatalog
{
    public int SchemaVersion { get; init; }

    public IReadOnlyDictionary<string, DatabaseReadProfile>? Profiles { get; init; }
}

internal sealed class DatabaseReadProfile
{
    public string? Provider { get; init; }

    public string? ConfigurationPath { get; init; }

    public string? Environment { get; init; }

    public string? ConnectionStringName { get; init; }

    public int? MaximumRows { get; init; }

    public int? MaximumTimeoutSeconds { get; init; }

    public int? MaximumLockTimeoutMilliseconds { get; init; }

    public int? MaximumOutputBytes { get; init; }

    public int? MaximumCellBytes { get; init; }

    public int? MaximumSqlBytes { get; init; }
}

internal sealed record ResolvedDatabaseReadProfile(
    string Name,
    DatabaseProviderKind Provider,
    string ConfigurationPath,
    string Environment,
    string ConnectionStringName,
    DatabaseReadLimits MaximumLimits);

internal sealed record DatabaseReadLimits(
    int MaximumRows,
    int TimeoutSeconds,
    int LockTimeoutMilliseconds,
    int MaximumOutputBytes,
    int MaximumCellBytes,
    int MaximumSqlBytes);

internal enum DatabaseProviderKind
{
    SqlServer,
    PostgreSql
}

internal sealed class DatabaseReadSuccessResponse
{
    public int SchemaVersion { get; init; } = 1;

    public bool Ok { get; init; } = true;

    public required string Operation { get; init; }

    public required string RequestId { get; init; }

    public required DatabaseReadTargetResponse Target { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DatabaseQueryResultResponse? Result { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DatabaseReadValidationResponse? Validation { get; init; }

    public required DatabaseReadTimingResponse Timing { get; init; }
}

internal sealed class DatabaseReadTargetResponse
{
    public required string Profile { get; init; }

    public required string Provider { get; init; }

    public required string Environment { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? ReadOnlyVerified { get; init; }
}

internal sealed class DatabaseReadValidationResponse
{
    public bool RequestAccepted { get; init; } = true;

    public required DatabaseReadLimitsResponse EffectiveLimits { get; init; }
}

internal sealed class DatabaseQueryResultResponse
{
    public required IReadOnlyList<DatabaseReadColumnResponse> Columns { get; init; }

    public required List<IReadOnlyList<object?>> Rows { get; init; }

    public int RowCount => Rows.Count;

    public bool Truncated { get; set; }

    public int TruncatedCellCount { get; init; }
}

internal sealed class DatabaseReadColumnResponse
{
    public required string Name { get; init; }

    public required string ProviderType { get; init; }

    public bool AllowsNull { get; init; }
}

internal sealed class DatabaseReadLimitsResponse
{
    public int MaximumRows { get; init; }

    public int TimeoutSeconds { get; init; }

    public int MaximumOutputBytes { get; init; }

    public int MaximumCellBytes { get; init; }
}

internal sealed class DatabaseReadTimingResponse
{
    public long ElapsedMilliseconds { get; init; }
}

internal sealed class DatabaseReadErrorResponse
{
    public int SchemaVersion { get; init; } = 1;

    public bool Ok { get; init; }

    public required string RequestId { get; init; }

    public required DatabaseReadError Error { get; init; }
}

internal sealed class DatabaseReadError
{
    public required string Code { get; init; }

    public required string Message { get; init; }
}
