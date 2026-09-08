using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using NewHeap.Platform.AspNet.Common.DAL;

namespace NewHeap.Platform.AspNet.Common.SqlServer;

/// <summary>Configures SqlServer and its NewHeap repository operations for a DbContext.</summary>
public static class SqlServerDbContextOptionsExtensions
{
    public static DbContextOptionsBuilder UseNewHeapSqlServer(
        this DbContextOptionsBuilder optionsBuilder,
        string connectionString,
        Action<SqlServerDbContextOptionsBuilder>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(optionsBuilder);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        optionsBuilder.UseSqlServer(connectionString, configure);
        NhRepositoryProviderOptionsExtension.Configure(optionsBuilder, SqlServerRepositoryProvider.Instance);
        return optionsBuilder;
    }

    public static DbContextOptionsBuilder<TContext> UseNewHeapSqlServer<TContext>(
        this DbContextOptionsBuilder<TContext> optionsBuilder,
        string connectionString,
        Action<SqlServerDbContextOptionsBuilder>? configure = null)
        where TContext : DbContext
    {
        UseNewHeapSqlServer((DbContextOptionsBuilder)optionsBuilder, connectionString, configure);
        return optionsBuilder;
    }
}
