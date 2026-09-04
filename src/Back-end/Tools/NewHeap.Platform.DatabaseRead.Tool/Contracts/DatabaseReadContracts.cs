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

    public DatabaseSchemaRequest? Schema { get; init; }

    public string? Reason { get; init; }
}

internal sealed class DatabaseSchemaRequest
{
    public string? Operation { get; init; }

    public string? SchemaName { get; init; }

    public string? ObjectName { get; init; }

    public string? SearchTerm { get; init; }
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

internal enum DatabaseReadRequestKind
{
    Query,
    Schema
}

internal enum DatabaseSchemaOperation
{
    Search,
    SearchAndDescribe,
    Describe,
    Indexes
}

internal sealed record ResolvedDatabaseSchemaRequest(
    DatabaseSchemaOperation Operation,
    string? SchemaName,
    string? ObjectName,
    string? SearchTerm);

internal sealed record ValidatedDatabaseReadRequest(
    DatabaseReadRequestKind Kind,
    DatabaseReadLimits Limits,
    ResolvedDatabaseSchemaRequest? Schema);

internal sealed class DatabaseReadSuccessResponse
{
    public int SchemaVersion { get; init; } = 1;

    public bool Ok { get; init; } = true;

    public required string Operation { get; init; }

    public required string RequestId { get; init; }

    public required DatabaseReadTargetResponse Target { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? RequiredCapabilities { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DatabaseQueryResultResponse? Result { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DatabaseReadValidationResponse? Validation { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DatabaseSchemaResultResponse? Schema { get; init; }

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

internal sealed class DatabaseSchemaResultResponse
{
    public required string Operation { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<DatabaseSchemaObjectSummaryResponse>? Objects { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DatabaseSchemaObjectResponse? Object { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DatabaseSchemaIndexesResponse? Indexes { get; init; }

    public bool Truncated { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? EvidenceHash { get; set; }
}

internal sealed class DatabaseSchemaObjectSummaryResponse
{
    public required string Schema { get; init; }

    public required string Name { get; init; }

    public required string Kind { get; init; }

    public required string SqlIdentifier { get; init; }
}

internal sealed class DatabaseSchemaObjectResponse
{
    public required string Schema { get; init; }

    public required string Name { get; init; }

    public required string Kind { get; init; }

    public required string SqlIdentifier { get; init; }

    public required IReadOnlyList<DatabaseSchemaColumnResponse> Columns { get; init; }

    public required IReadOnlyList<DatabaseSchemaIndexResponse> Indexes { get; init; }

    public required DatabaseSchemaRelationshipsResponse Relationships { get; init; }
}

internal sealed class DatabaseSchemaRelationshipsResponse
{
    public required IReadOnlyList<DatabaseSchemaRelationshipResponse> Outgoing { get; init; }

    public required IReadOnlyList<DatabaseSchemaRelationshipResponse> Incoming { get; init; }
}

internal sealed class DatabaseSchemaRelationshipResponse
{
    public required string Name { get; init; }

    public bool IsValidated { get; init; }

    public required DatabaseSchemaRelationshipObjectResponse Source { get; init; }

    public required DatabaseSchemaRelationshipObjectResponse Target { get; init; }

    public required IReadOnlyList<DatabaseSchemaRelationshipColumnPairResponse> ColumnPairs { get; init; }
}

internal sealed class DatabaseSchemaRelationshipObjectResponse
{
    public required string Schema { get; init; }

    public required string Name { get; init; }

    public required string SqlIdentifier { get; init; }
}

internal sealed class DatabaseSchemaRelationshipColumnPairResponse
{
    public int Position { get; init; }

    public required string SourceColumn { get; init; }

    public required string TargetColumn { get; init; }
}

internal sealed class DatabaseSchemaIndexesResponse
{
    public required string Schema { get; init; }

    public required string Name { get; init; }

    public required string Kind { get; init; }

    public required string SqlIdentifier { get; init; }

    public required IReadOnlyList<DatabaseSchemaIndexResponse> Items { get; init; }
}

internal sealed class DatabaseSchemaColumnResponse
{
    public int Ordinal { get; init; }

    public required string Name { get; init; }

    public required string ProviderType { get; init; }

    public bool AllowsNull { get; init; }

    public bool IsPrimaryKey { get; init; }
}

internal sealed class DatabaseSchemaIndexResponse
{
    public required string Name { get; init; }

    public required string AccessMethod { get; init; }

    public bool IsUnique { get; init; }

    public bool IsPrimaryKey { get; init; }

    public bool IsPartial { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Predicate { get; init; }

    public required IReadOnlyList<string> Columns { get; init; }

    public required IReadOnlyList<DatabaseSchemaIndexColumnResponse> KeyColumns { get; init; }

    public required IReadOnlyList<string> IncludedColumns { get; init; }
}

internal sealed class DatabaseSchemaIndexColumnResponse
{
    public int Position { get; init; }

    public required string Kind { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Name { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Expression { get; init; }

    public required string Direction { get; init; }
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

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Classification { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Provider { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ProviderCode { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Transient { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Stage { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RetryHint { get; init; }
}
