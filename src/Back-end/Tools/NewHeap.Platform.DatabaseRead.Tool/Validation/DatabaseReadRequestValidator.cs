using System.Text;
using System.Text.RegularExpressions;

namespace NewHeap.Platform.DatabaseRead;

internal static partial class DatabaseReadRequestValidator
{
    private const int MaximumParameterCount = 128;

    public static DatabaseReadLimits Validate(
        DatabaseReadRequest request,
        ResolvedDatabaseReadProfile profile)
    {
        if (request.SchemaVersion != 1)
        {
            throw Invalid("unsupported-schema-version", "The request must use schemaVersion 1.");
        }

        if (!string.Equals(request.Profile, profile.Name, StringComparison.OrdinalIgnoreCase))
        {
            throw Invalid("profile-mismatch", "The request profile does not match the selected profile.");
        }

        if (string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Length > 256)
        {
            throw Invalid("invalid-reason", "Reason is required and may contain at most 256 characters.");
        }

        if (string.IsNullOrWhiteSpace(request.Sql))
        {
            throw Invalid("empty-sql", "The SQL query is required.");
        }

        var sqlBytes = Encoding.UTF8.GetByteCount(request.Sql);
        if (sqlBytes > profile.MaximumLimits.MaximumSqlBytes)
        {
            throw Invalid(
                "sql-too-large",
                $"The SQL query exceeds the profile limit of {profile.MaximumLimits.MaximumSqlBytes} bytes.");
        }

        ValidateParameters(request.Parameters ?? []);

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

        SqlReadOnlyPolicy.Validate(request.Sql, profile.Provider);

        return profile.MaximumLimits with
        {
            MaximumRows = requestedMaximumRows,
            TimeoutSeconds = requestedTimeout
        };
    }

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
