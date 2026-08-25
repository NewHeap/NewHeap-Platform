using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Text.Json;

namespace NewHeap.Platform.DatabaseRead;

internal static class DatabaseReadParameterBinder
{
    public static void AddParameters(
        DbCommand command,
        IReadOnlyList<DatabaseReadParameterRequest> parameterRequests)
    {
        foreach (var request in parameterRequests)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = $"@{request.Name}";
            parameter.DbType = ParseType(request.Type);
            parameter.Value = ConvertValue(request, parameter.DbType);
            command.Parameters.Add(parameter);
        }
    }

    public static DbType ParseType(string? type)
    {
        return type?.ToLowerInvariant() switch
        {
            "string" => DbType.String,
            "boolean" => DbType.Boolean,
            "int32" => DbType.Int32,
            "int64" => DbType.Int64,
            "decimal" => DbType.Decimal,
            "double" => DbType.Double,
            "uuid" => DbType.Guid,
            "date-time" => DbType.DateTimeOffset,
            "date" => DbType.Date,
            "binary-base64" => DbType.Binary,
            _ => throw new DatabaseReadExpectedException(
                "unsupported-parameter-type",
                $"Parameter type '{type}' is not supported.",
                DatabaseReadExitCode.InvalidRequest)
        };
    }

    public static void ValidateValue(DatabaseReadParameterRequest request)
    {
        var type = ParseType(request.Type);

        _ = ConvertValue(request, type);
    }

    private static object ConvertValue(DatabaseReadParameterRequest request, DbType type)
    {
        if (request.Value.ValueKind == JsonValueKind.Null)
        {
            return DBNull.Value;
        }

        try
        {
            return type switch
            {
                DbType.String => request.Value.GetString()!,
                DbType.Boolean => request.Value.GetBoolean(),
                DbType.Int32 => request.Value.GetInt32(),
                DbType.Int64 => ParseInt64(request.Value),
                DbType.Decimal => ParseDecimal(request.Value),
                DbType.Double => request.Value.GetDouble(),
                DbType.Guid => Guid.Parse(request.Value.GetString()!),
                DbType.DateTimeOffset => request.Value.GetDateTimeOffset(),
                DbType.Date => DateOnly.ParseExact(
                    request.Value.GetString()!,
                    "yyyy-MM-dd",
                    CultureInfo.InvariantCulture),
                DbType.Binary => Convert.FromBase64String(request.Value.GetString()!),
                _ => throw new InvalidOperationException($"Unsupported DbType {type}.")
            };
        }
        catch (Exception exception) when (exception is FormatException or InvalidOperationException or OverflowException)
        {
            throw new DatabaseReadExpectedException(
                "invalid-parameter-value",
                $"Parameter '{request.Name}' does not contain a valid {request.Type} value.",
                DatabaseReadExitCode.InvalidRequest);
        }
    }

    private static long ParseInt64(JsonElement value)
    {
        return value.ValueKind == JsonValueKind.String
            ? long.Parse(value.GetString()!, NumberStyles.Integer, CultureInfo.InvariantCulture)
            : value.GetInt64();
    }

    private static decimal ParseDecimal(JsonElement value)
    {
        return value.ValueKind == JsonValueKind.String
            ? decimal.Parse(value.GetString()!, NumberStyles.Number, CultureInfo.InvariantCulture)
            : value.GetDecimal();
    }
}
