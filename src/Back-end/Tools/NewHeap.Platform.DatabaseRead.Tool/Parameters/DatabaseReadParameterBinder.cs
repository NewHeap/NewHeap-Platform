using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Text.Json;

namespace NewHeap.Platform.DatabaseRead;

internal static class DatabaseReadParameterBinder
{
    /// <summary>
    /// Adds the typed parameters. A <c>date-time</c> value with <c>Z</c> or an offset is an instant,
    /// which the provider binds in the form its driver accepts; one with neither is a wall-clock
    /// value, which binds unshifted as the provider's plain date and time type. Neither depends on
    /// the time zone of the host the tool runs on.
    /// </summary>
    public static void AddParameters(
        DbCommand command,
        IDatabaseReadProvider provider,
        IReadOnlyList<DatabaseReadParameterRequest> parameterRequests)
    {
        foreach (var request in parameterRequests)
        {
            var type = ParseType(request.Type);
            var value = ConvertValue(request, type);

            var parameter = command.CreateParameter();
            parameter.ParameterName = $"@{request.Name}";
            parameter.DbType = value is DateTime ? DbType.DateTime2 : type;
            parameter.Value = value is DateTimeOffset instant ? provider.NormalizeInstant(instant) : value;
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
                DbType.DateTimeOffset => ParseDateTime(request.Value),
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

    /// <summary>
    /// A <see cref="DateTimeOffset"/> for a value with <c>Z</c> or an offset, and a
    /// <see cref="DateTime"/> of unspecified kind for a value with neither, which
    /// <see cref="JsonElement.GetDateTimeOffset"/> would place in the host's time zone. These are
    /// two branches because a conditional expression would convert the
    /// <see cref="DateTime"/> to a <see cref="DateTimeOffset"/>, with that same result.
    /// </summary>
    private static object ParseDateTime(JsonElement value)
    {
        var parsed = value.GetDateTime();
        if (parsed.Kind == DateTimeKind.Unspecified)
        {
            return parsed;
        }

        return value.GetDateTimeOffset();
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
