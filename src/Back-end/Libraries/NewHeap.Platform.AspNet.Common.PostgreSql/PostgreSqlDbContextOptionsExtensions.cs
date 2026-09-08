using Microsoft.EntityFrameworkCore;
using Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure;
using NewHeap.Platform.AspNet.Common.DAL;

namespace NewHeap.Platform.AspNet.Common.PostgreSql;

/// <summary>Configures PostgreSql and its NewHeap repository operations for a DbContext.</summary>
public static class PostgreSqlDbContextOptionsExtensions
{
    public static DbContextOptionsBuilder UseNewHeapPostgreSql(
        this DbContextOptionsBuilder optionsBuilder,
        string connectionString,
        Action<NpgsqlDbContextOptionsBuilder>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(optionsBuilder);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        optionsBuilder.UseNpgsql(connectionString, configure);
        NhRepositoryProviderOptionsExtension.Configure(optionsBuilder, PostgreSqlRepositoryProvider.Instance);
        return optionsBuilder;
    }

    public static DbContextOptionsBuilder<TContext> UseNewHeapPostgreSql<TContext>(
        this DbContextOptionsBuilder<TContext> optionsBuilder,
        string connectionString,
        Action<NpgsqlDbContextOptionsBuilder>? configure = null)
        where TContext : DbContext
    {
        UseNewHeapPostgreSql((DbContextOptionsBuilder)optionsBuilder, connectionString, configure);
        return optionsBuilder;
    }
}
