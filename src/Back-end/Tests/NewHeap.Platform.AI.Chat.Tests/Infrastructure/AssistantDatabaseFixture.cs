using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.AI.Chat.Persistence;
using Testcontainers.MsSql;
using Testcontainers.PostgreSql;
using Xunit;

namespace NewHeap.Platform.AI.Chat.Tests.Infrastructure;

public enum AssistantTestProvider
{
    SqlServer = 0,
    PostgreSql = 1
}

/// <summary>
/// Starts one real SQL Server and one real PostgreSQL container for the relational assistant tests.
/// Each test isolates itself in its own schema, which also exercises schema routing of the
/// checked-in migrations.
/// </summary>
public sealed class AssistantDatabaseFixture : IAsyncLifetime
{
    private readonly MsSqlContainer _sqlServer = new MsSqlBuilder(
        "mcr.microsoft.com/mssql/server:2022-CU14-ubuntu-22.04").Build();

    private readonly PostgreSqlContainer _postgreSql = new PostgreSqlBuilder("postgres:16-alpine").Build();

    public async Task InitializeAsync()
    {
        await Task.WhenAll(_sqlServer.StartAsync(), _postgreSql.StartAsync());
    }

    public async Task DisposeAsync()
    {
        await _sqlServer.DisposeAsync();
        await _postgreSql.DisposeAsync();
    }

    public string ConnectionString(AssistantTestProvider provider)
    {
        return provider == AssistantTestProvider.SqlServer
            ? _sqlServer.GetConnectionString()
            : _postgreSql.GetConnectionString();
    }

    internal NhAssistantStorageRegistration CreateRegistration(
        AssistantTestProvider provider,
        string schema)
    {
        var connectionString = ConnectionString(provider);
        return provider == AssistantTestProvider.SqlServer
            ? NhAssistantSqlServerStorage.CreateRegistration(
                _ => connectionString,
                options => options.Schema = schema)
            : NhAssistantPostgreSqlStorage.CreateRegistration(
                _ => connectionString,
                options => options.Schema = schema);
    }

    /// <summary>
    /// Registers storage for a fresh schema and applies the checked-in migrations.
    /// </summary>
    internal async Task<ServiceProvider> CreateMigratedStorageAsync(
        AssistantTestProvider provider,
        string? schema = null,
        Action<IServiceCollection>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        NhAssistantStorage.Register(
            services,
            CreateRegistration(provider, schema ?? UniqueSchema()));
        services.AddSingleton<INhAssistantStore, NhAssistantStore>();
        configure?.Invoke(services);
        var serviceProvider = services.BuildServiceProvider();
        await using var context = serviceProvider
            .GetRequiredService<NhAssistantDbContextFactory>()
            .CreateDbContext();
        await context.Database.MigrateAsync();
        return serviceProvider;
    }

    public static string UniqueSchema()
    {
        return "t" + Guid.NewGuid().ToString("N")[..12];
    }
}

[CollectionDefinition(Name)]
public sealed class AssistantDatabaseCollection : ICollectionFixture<AssistantDatabaseFixture>
{
    public const string Name = "assistant-database";
}
