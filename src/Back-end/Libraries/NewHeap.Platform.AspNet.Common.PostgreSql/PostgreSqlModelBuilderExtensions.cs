using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace NewHeap.Platform.AspNet.Common.DAL;

public static class PostgreSqlModelBuilderExtensions
{
    /// <summary>
    /// Prevents SQL Server-specific column declarations from reaching an Npgsql model.
    /// </summary>
    public static void ValidatePostgreSqlColumnTypes(this ModelBuilder modelBuilder, string? providerName)
    {
        if (!string.Equals(providerName, DatabaseProviderConfigurationExtensions.PostgreSqlProviderName, StringComparison.Ordinal))
        {
            return;
        }

        foreach (var property in modelBuilder.Model.GetEntityTypes().SelectMany(x => x.GetProperties()))
        {
            var columnType = property.GetColumnType();
            if (string.IsNullOrWhiteSpace(columnType))
            {
                continue;
            }

            var normalizedColumnType = columnType.Replace(" ", string.Empty).ToLowerInvariant();
            if (normalizedColumnType == "nvarchar(max)")
            {
                property.SetColumnType("text");
                continue;
            }

            if (IsSqlServerSpecificColumnType(normalizedColumnType))
            {
                throw new InvalidOperationException(
                    $"PostgreSQL cannot use SQL Server column type '{columnType}' for " +
                    $"'{property.DeclaringType.DisplayName()}.{property.Name}'. " +
                    "Use provider-neutral data annotations or Fluent API configuration instead.");
            }
        }
    }

    private static bool IsSqlServerSpecificColumnType(string columnType)
    {
        return columnType.StartsWith("nvarchar(", StringComparison.Ordinal)
               || columnType.StartsWith("nchar(", StringComparison.Ordinal)
               || columnType.StartsWith("binary(", StringComparison.Ordinal)
               || columnType is "uniqueidentifier" or "datetimeoffset" or "datetime2" or "smalldatetime"
                   or "bit" or "money" or "smallmoney" or "sql_variant" or "hierarchyid"
                   or "geography" or "geometry";
    }
}
