using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure;
using NewHeap.Platform.AspNet.Common.DAL;

namespace NewHeap.Platform.AspNet.Common.PostgreSql;

/// <summary>Configures PostgreSql and its NewHeap repository operations for a DbContext.</summary>
public static class PostgreSqlDbContextOptionsExtensions
{
    /// <summary>
    /// Configures the context for PostgreSql with the NewHeap repository operations. Collection search
    /// and the <c>LIKE</c> filter match case-insensitively with <c>ILIKE</c>, as SQL Server's default
    /// collation does; call <see cref="UseNewHeapCaseSensitiveCollectionTextMatch"/> in
    /// <paramref name="configure"/> to keep PostgreSQL's case-sensitive <c>LIKE</c>.
    /// </summary>
    public static DbContextOptionsBuilder UseNewHeapPostgreSql(
        this DbContextOptionsBuilder optionsBuilder,
        string connectionString,
        Action<NpgsqlDbContextOptionsBuilder>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(optionsBuilder);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        optionsBuilder.UseNpgsql(connectionString, configure);
        NhRepositoryProviderOptionsExtension.Configure(optionsBuilder, PostgreSqlRepositoryProvider.Instance);
        PostgreSqlCollectionTextMatchOptionsExtension.ConfigureDefault(optionsBuilder);
        return optionsBuilder;
    }

    /// <inheritdoc cref="UseNewHeapPostgreSql(DbContextOptionsBuilder, string, Action{NpgsqlDbContextOptionsBuilder}?)"/>
    public static DbContextOptionsBuilder<TContext> UseNewHeapPostgreSql<TContext>(
        this DbContextOptionsBuilder<TContext> optionsBuilder,
        string connectionString,
        Action<NpgsqlDbContextOptionsBuilder>? configure = null)
        where TContext : DbContext
    {
        UseNewHeapPostgreSql((DbContextOptionsBuilder)optionsBuilder, connectionString, configure);
        return optionsBuilder;
    }

    /// <summary>
    /// Keeps PostgreSQL's case-sensitive <c>LIKE</c> for collection search, the <c>LIKE</c> filter and
    /// other <see cref="NewHeap.Platform.Common.Services.NhCollectionTextMatch"/> matches, instead of the
    /// case-insensitive <c>ILIKE</c> that <c>UseNewHeapPostgreSql</c> configures. Call it in the
    /// <c>configure</c> action of <c>UseNewHeapPostgreSql</c>. The same search then finds fewer rows on
    /// PostgreSQL than on a case-insensitive SQL Server collation.
    /// </summary>
    public static NpgsqlDbContextOptionsBuilder UseNewHeapCaseSensitiveCollectionTextMatch(
        this NpgsqlDbContextOptionsBuilder postgreSqlOptionsBuilder)
    {
        ArgumentNullException.ThrowIfNull(postgreSqlOptionsBuilder);

        var optionsBuilder = ((IRelationalDbContextOptionsBuilderInfrastructure)postgreSqlOptionsBuilder).OptionsBuilder;
        PostgreSqlCollectionTextMatchOptionsExtension.Configure(optionsBuilder, caseSensitive: true);
        return postgreSqlOptionsBuilder;
    }
}
