using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.Configuration;

namespace NewHeap.Platform.AspNet.Common.DAL;

public enum DatabaseProvider
{
    SqlServer,
    PostgreSql
}

/// <summary>
/// Applies the configured relational database provider consistently to EF Core contexts.
/// </summary>
public static class DatabaseProviderConfigurationExtensions
{
    public const string PostgreSqlProviderName = "Npgsql.EntityFrameworkCore.PostgreSQL";

    public static DatabaseProvider GetDatabaseProvider(this IConfiguration configuration)
    {
        var provider = configuration["Database:Provider"];
        if (string.IsNullOrWhiteSpace(provider))
        {
            return DatabaseProvider.SqlServer;
        }

        return provider.Trim().ToLowerInvariant() switch
        {
            "sql-server" or "sqlserver" => DatabaseProvider.SqlServer,
            "postgresql" or "postgres" => DatabaseProvider.PostgreSql,
            _ => throw new InvalidOperationException(
                $"Unsupported database provider '{provider}'. Supported values are 'sql-server' and 'postgresql'.")
        };
    }

    public static string GetDatabaseConnectionString(this IConfiguration configuration)
    {
        var connectionStringName = configuration["Database:ConnectionStringName"] ?? "DefaultConnection";
        return configuration.GetConnectionString(connectionStringName)
            ?? throw new InvalidOperationException($"Connection string '{connectionStringName}' is not configured.");
    }

    public static DbContextOptionsBuilder UseConfiguredDatabase(
        this DbContextOptionsBuilder optionsBuilder,
        IConfiguration configuration,
        int? commandTimeoutSeconds = null)
    {
        var connectionString = configuration.GetDatabaseConnectionString();

        switch (configuration.GetDatabaseProvider())
        {
            case DatabaseProvider.SqlServer:
                optionsBuilder.UseSqlServer(connectionString, options =>
                {
                    if (commandTimeoutSeconds.HasValue)
                    {
                        options.CommandTimeout(commandTimeoutSeconds);
                    }
                });
                break;
            case DatabaseProvider.PostgreSql:
                optionsBuilder.UseNpgsql(connectionString, options =>
                {
                    if (commandTimeoutSeconds.HasValue)
                    {
                        options.CommandTimeout(commandTimeoutSeconds);
                    }

                    var migrationsAssembly = configuration["Database:PostgreSqlMigrationsAssembly"];
                    if (!string.IsNullOrWhiteSpace(migrationsAssembly))
                    {
                        options.MigrationsAssembly(migrationsAssembly);
                    }
                });
                break;
            default:
                throw new ArgumentOutOfRangeException();
        }

        return optionsBuilder;
    }

    /// <summary>
    /// Prevents SQL Server-specific column declarations from reaching an Npgsql model.
    /// </summary>
    public static void ValidatePostgreSqlColumnTypes(this ModelBuilder modelBuilder, string? providerName)
    {
        if (!string.Equals(providerName, PostgreSqlProviderName, StringComparison.Ordinal))
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
