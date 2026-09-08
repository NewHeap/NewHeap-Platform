using Microsoft.Extensions.Configuration;

namespace NewHeap.Platform.AspNet.Common.DAL;

public enum DatabaseProvider
{
    SqlServer,
    PostgreSql
}

/// <summary>
/// Reads provider selection and connection strings without referencing a database implementation.
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
}
