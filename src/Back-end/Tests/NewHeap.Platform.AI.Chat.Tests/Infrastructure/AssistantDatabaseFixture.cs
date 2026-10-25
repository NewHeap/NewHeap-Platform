using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.Tests.Relational;

namespace NewHeap.Platform.AI.Chat.Tests.Infrastructure;

public enum AssistantTestProvider
{
    SqlServer = 0,
    PostgreSql = 1
}

/// <summary>
/// Gives a test class access to the real SQL Server and PostgreSQL servers of the relational
/// assistant tests. It holds no state of its own: the servers (see <see cref="RelationalTestServer"/>)
/// and the pools of migrated schemas (see <see cref="AssistantSchemaPool"/>) belong to the test
/// process, so test classes that use this fixture run in parallel and a server only starts when a
/// test uses its provider.
/// </summary>
public sealed class AssistantDatabaseFixture
{
    /// <summary>
    /// The connection string of the default database of the provider's server, which starts on first
    /// use. A leased schema can live in another database of the server; its lease has that connection
    /// string.
    /// </summary>
    public Task<string> ConnectionStringAsync(AssistantTestProvider provider)
    {
        return RelationalTestServer.For(ToRelational(provider)).GetConnectionStringAsync();
    }

    /// <summary>
    /// Leases a migrated, empty schema of its own for one host. Dispose the lease when the host is
    /// disposed; the schema then returns to the pool.
    /// </summary>
    internal Task<RelationalTestLease> LeaseSchemaAsync(AssistantTestProvider provider)
    {
        return AssistantSchemaPool.LeaseAsync(provider);
    }

    /// <summary>
    /// Registers storage over a leased, migrated and empty schema. Disposing the returned provider
    /// returns the schema to the pool.
    /// </summary>
    internal async Task<ServiceProvider> CreateStorageAsync(
        AssistantTestProvider provider,
        Action<IServiceCollection>? configure = null)
    {
        var lease = await LeaseSchemaAsync(provider);
        try
        {
            var services = CreateStorageServices(provider, lease.ConnectionString, lease.Schema, configure);

            // The provider owns the lease: it creates the singleton itself and is asked for it right
            // away, so disposing the provider disposes the lease. Created first, it is disposed last.
            services.AddSingleton(_ => lease);
            var serviceProvider = services.BuildServiceProvider();
            serviceProvider.GetRequiredService<RelationalTestLease>();
            return serviceProvider;
        }
        catch
        {
            await lease.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// Registers storage for <paramref name="schema"/> and applies the checked-in migrations to it,
    /// outside the pool. Only for tests that prove the migrations or their schema routing themselves.
    /// </summary>
    internal async Task<ServiceProvider> CreateMigratedStorageAsync(
        AssistantTestProvider provider,
        string schema)
    {
        var connectionString = await ConnectionStringAsync(provider);
        var serviceProvider = CreateStorageServices(provider, connectionString, schema, null).BuildServiceProvider();
        try
        {
            await MigrateAsync(serviceProvider);
            return serviceProvider;
        }
        catch
        {
            await serviceProvider.DisposeAsync();
            throw;
        }
    }

    internal static RelationalTestProvider ToRelational(AssistantTestProvider provider)
    {
        return provider == AssistantTestProvider.SqlServer
            ? RelationalTestProvider.SqlServer
            : RelationalTestProvider.PostgreSql;
    }

    public static string UniqueSchema()
    {
        return "t" + Guid.NewGuid().ToString("N")[..12];
    }

    internal static NhAssistantStorageRegistration CreateRegistration(
        AssistantTestProvider provider,
        string connectionString,
        string schema)
    {
        return provider == AssistantTestProvider.SqlServer
            ? NhAssistantSqlServerStorage.CreateRegistration(
                _ => connectionString,
                options => options.Schema = schema)
            : NhAssistantPostgreSqlStorage.CreateRegistration(
                _ => connectionString,
                options => options.Schema = schema);
    }

    internal static ServiceCollection CreateStorageServices(
        AssistantTestProvider provider,
        string connectionString,
        string schema,
        Action<IServiceCollection>? configure)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        NhAssistantStorage.Register(services, CreateRegistration(provider, connectionString, schema));
        services.AddSingleton<INhAssistantStore, NhAssistantStore>();
        configure?.Invoke(services);
        return services;
    }

    /// <summary>
    /// Applies the checked-in migrations. Their commands may run as long as the other commands that
    /// prepare test databases (<see cref="RelationalTestServer.PreparationCommandTimeout"/>), so a
    /// server slowed down by the parallel test matrix fails no test while it migrates a slot.
    /// </summary>
    internal static async Task MigrateAsync(IServiceProvider services)
    {
        await using var context = services
            .GetRequiredService<NhAssistantDbContextFactory>()
            .CreateDbContext();
        context.Database.SetCommandTimeout(RelationalTestServer.PreparationCommandTimeout);
        await context.Database.MigrateAsync();
    }
}
