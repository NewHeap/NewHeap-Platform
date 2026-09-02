using System.Text;
using System.Text.RegularExpressions;

namespace NewHeap.Platform.DatabaseRead;

internal static partial class DatabaseReadRequestValidator
{
    private const int MaximumParameterCount = 128;

    public static ValidatedDatabaseReadRequest Validate(
        DatabaseReadRequest request,
        ResolvedDatabaseReadProfile profile,
        DatabaseReadCommand command)
    {
        if (request.SchemaVersion != 1)
        {
            throw Invalid("unsupported-schema-version", "The request must use schemaVersion 1.");
        }

        if (!string.IsNullOrWhiteSpace(request.Profile) &&
            !string.Equals(request.Profile, profile.Name, StringComparison.OrdinalIgnoreCase))
        {
            throw Invalid("profile-mismatch", "The request profile does not match the selected profile.");
        }

        if (string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Length > 2_000)
        {
            throw Invalid("invalid-reason", "Reason is required and may contain at most 2000 characters.");
        }

        var kind = ResolveRequestKind(request, command);
        var schema = kind == DatabaseReadRequestKind.Schema
            ? ValidateSchema(request)
            : null;

        if (kind == DatabaseReadRequestKind.Query)
        {
            ValidateQuery(request, profile);
        }

        var requestedMaximumRows = request.Limits?.MaximumRows ?? profile.MaximumLimits.MaximumRows;
        var requestedTimeout = request.Limits?.TimeoutSeconds ?? profile.MaximumLimits.TimeoutSeconds;

        if (requestedMaximumRows <= 0 || requestedMaximumRows > profile.MaximumLimits.MaximumRows)
        {
            throw Invalid(
                "invalid-row-limit",
                $"maximumRows must be between 1 and the profile maximum of {profile.MaximumLimits.MaximumRows}.");
        }

        if (requestedTimeout <= 0 || requestedTimeout > profile.MaximumLimits.TimeoutSeconds)
        {
            throw Invalid(
                "invalid-timeout",
                $"timeoutSeconds must be between 1 and the profile maximum of {profile.MaximumLimits.TimeoutSeconds}.");
        }

        var limits = profile.MaximumLimits with
        {
            MaximumRows = requestedMaximumRows,
            TimeoutSeconds = requestedTimeout
        };

        return new ValidatedDatabaseReadRequest(kind, limits, schema);
    }

    private static DatabaseReadRequestKind ResolveRequestKind(
        DatabaseReadRequest request,
        DatabaseReadCommand command)
    {
        var hasSql = !string.IsNullOrWhiteSpace(request.Sql);
        var hasSchema = request.Schema is not null;

        if (!hasSql && !hasSchema)
        {
            throw command == DatabaseReadCommand.Schema
                ? Invalid("schema-payload-required", "The schema command requires a schema request.")
                : Invalid("empty-sql", "The SQL query is required.");
        }

        if (hasSql && hasSchema)
        {
            throw Invalid(
                "invalid-operation-payload",
                "Supply exactly one SQL query or schema request.");
        }

        if (command == DatabaseReadCommand.Query && !hasSql)
        {
            throw Invalid("query-payload-required", "The query command requires a SQL query request.");
        }

        if (command == DatabaseReadCommand.Schema && !hasSchema)
        {
            throw Invalid("schema-payload-required", "The schema command requires a schema request.");
        }

        return hasSchema ? DatabaseReadRequestKind.Schema : DatabaseReadRequestKind.Query;
    }

    private static void ValidateQuery(
        DatabaseReadRequest request,
        ResolvedDatabaseReadProfile profile)
    {
        var sqlBytes = Encoding.UTF8.GetByteCount(request.Sql!);
        if (sqlBytes > profile.MaximumLimits.MaximumSqlBytes)
        {
            throw Invalid(
                "sql-too-large",
                $"The SQL query exceeds the profile limit of {profile.MaximumLimits.MaximumSqlBytes} bytes.");
        }

        ValidateParameters(request.Parameters ?? []);
        SqlReadOnlyPolicy.Validate(request.Sql!, profile.Provider);
    }

    private static ResolvedDatabaseSchemaRequest ValidateSchema(DatabaseReadRequest request)
    {
        if ((request.Parameters?.Count ?? 0) > 0)
        {
            throw Invalid(
                "schema-parameters-not-supported",
                "Schema requests do not accept SQL parameters.");
        }

        var schema = request.Schema!;
        var operation = schema.Operation?.Trim().ToLowerInvariant() switch
        {
            "search" => DatabaseSchemaOperation.Search,
            "search-and-describe" => DatabaseSchemaOperation.SearchAndDescribe,
            "describe" => DatabaseSchemaOperation.Describe,
            "indexes" => DatabaseSchemaOperation.Indexes,
            _ => throw Invalid(
                "invalid-schema-operation",
                "Schema operation must be 'search', 'search-and-describe', 'describe' or 'indexes'.")
        };

        var requiresObject = operation is DatabaseSchemaOperation.Describe or DatabaseSchemaOperation.Indexes;
        ValidateSchemaValue(schema.SchemaName, "schemaName", required: requiresObject);
        ValidateSchemaValue(schema.ObjectName, "objectName", required: requiresObject);
        ValidateSchemaValue(schema.SearchTerm, "searchTerm", required: false);

        if (operation is DatabaseSchemaOperation.Search or DatabaseSchemaOperation.SearchAndDescribe &&
            !string.IsNullOrWhiteSpace(schema.ObjectName))
        {
            throw Invalid(
                "invalid-schema-search",
                "objectName is only accepted for the describe or indexes schema operation.");
        }

        return new ResolvedDatabaseSchemaRequest(
            operation,
            Normalize(schema.SchemaName),
            Normalize(schema.ObjectName),
            Normalize(schema.SearchTerm));
    }

    private static void ValidateSchemaValue(string? value, string propertyName, bool required)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            if (required)
            {
                throw Invalid(
                    "missing-schema-value",
                    $"Schema property '{propertyName}' is required for this operation.");
            }

            return;
        }

        if (value.Length > 256 || value.Any(char.IsControl))
        {
            throw Invalid(
                "invalid-schema-value",
                $"Schema property '{propertyName}' may contain at most 256 non-control characters.");
        }
    }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static void ValidateParameters(IReadOnlyList<DatabaseReadParameterRequest> parameters)
    {
        if (parameters.Count > MaximumParameterCount)
        {
            throw Invalid(
                "too-many-parameters",
                $"A request may contain at most {MaximumParameterCount} parameters.");
        }

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var parameter in parameters)
        {
            if (string.IsNullOrWhiteSpace(parameter.Name) || !ParameterName().IsMatch(parameter.Name))
            {
                throw Invalid(
                    "invalid-parameter-name",
                    "Parameter names must start with a letter or underscore and contain only letters, digits and underscores.");
            }

            if (!names.Add(parameter.Name))
            {
                throw Invalid("duplicate-parameter", $"Parameter '{parameter.Name}' is supplied more than once.");
            }

            if (parameter.Value.ValueKind == System.Text.Json.JsonValueKind.Undefined)
            {
                throw Invalid("missing-parameter-value", $"Parameter '{parameter.Name}' is missing value.");
            }

            DatabaseReadParameterBinder.ValidateValue(parameter);
        }
    }

    private static DatabaseReadExpectedException Invalid(string code, string message)
    {
        return new DatabaseReadExpectedException(code, message, DatabaseReadExitCode.InvalidRequest);
    }

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]{0,127}$", RegexOptions.CultureInvariant)]
    private static partial Regex ParameterName();
}
