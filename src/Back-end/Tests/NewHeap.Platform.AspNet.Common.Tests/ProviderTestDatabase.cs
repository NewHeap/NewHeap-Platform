using Microsoft.EntityFrameworkCore;
using NewHeap.Platform.AspNet.Common.PostgreSql;
using NewHeap.Platform.AspNet.Common.SqlServer;
using NewHeap.Platform.Tests.Relational;

namespace NewHeap.Platform.AspNet.Common.Tests;

/// <summary>
/// An empty database of its own for one provider test, on the provider's server that every test of
/// the process shares. The test creates its model in it, as it did in a container of its own, and
/// test classes run in parallel without seeing each other's tables, locks or rows.
/// </summary>
internal sealed class ProviderTestDatabase
{
    private ProviderTestDatabase(RelationalTestProvider provider, string connectionString)
    {
        Provider = provider;
        ConnectionString = connectionString;
    }

    public RelationalTestProvider Provider { get; }

    public string ConnectionString { get; }

    /// <summary>
    /// The provider's name as the tests write it into their data, such as user names and keys.
    /// </summary>
    public string Label => Provider == RelationalTestProvider.SqlServer ? "sql-server" : "postgresql";

    public static async Task<ProviderTestDatabase> CreateAsync(RelationalTestProvider provider)
    {
        var connectionString = await RelationalTestServer.For(provider).CreateDatabaseAsync("aspnet");
        return new ProviderTestDatabase(provider, connectionString);
    }

    /// <summary>
    /// Configures EF Core for this database with the NewHeap provider registration, as an
    /// application does.
    /// </summary>
    public void UseNewHeapProvider(DbContextOptionsBuilder options)
    {
        if (Provider == RelationalTestProvider.SqlServer)
        {
            options.UseNewHeapSqlServer(ConnectionString);
        }
        else
        {
            options.UseNewHeapPostgreSql(ConnectionString);
        }
    }
}
