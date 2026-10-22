using Microsoft.EntityFrameworkCore;
using Npgsql;
using SampleProjectManagement.DAL;
using Testcontainers.PostgreSql;
using Xunit;

[assembly: AssemblyFixture(typeof(SampleProjectManagement.Core.Tests.SamplePostgreSqlServer))]

namespace SampleProjectManagement.Core.Tests;

/// <summary>
/// One PostgreSQL container for the whole test run instead of one per test or sample host. Every
/// test that needs a real database uses a database of its own on it, so test classes run in
/// parallel without seeing each other's tables, rows or migrations: a sample host gets a migrated
/// sample database from <see cref="CreateMigratedDatabaseAsync"/>, and a test that proves migrations
/// creates an empty database with <see cref="CreateDatabaseAsync"/>.
/// </summary>
/// <remarks>
/// xUnit creates the fixture once for the test assembly and passes it to every test class and class
/// fixture that asks for it in its constructor. The container starts when a test first creates a
/// database, so a run without database tests starts none, and stops when the run ends.
/// </remarks>
public sealed class SamplePostgreSqlServer : IAsyncDisposable
{
    private const int TemplateCopyAttempts = 3;

    private readonly Lazy<Task<PostgreSqlContainer>> _container = new(StartAsync);
    private readonly Lazy<Task<string>> _migratedTemplate;
    private int _databaseCount;

    public SamplePostgreSqlServer()
    {
        _migratedTemplate = new Lazy<Task<string>>(CreateMigratedTemplateAsync);
    }

    /// <summary>
    /// Creates an empty database and returns its connection string. The database name starts with
    /// <paramref name="name"/>, written in lowercase letters, digits and underscores.
    /// </summary>
    public async Task<string> CreateDatabaseAsync(string name)
    {
        var database = NewDatabaseName(name);
        await ExecuteAsync($"CREATE DATABASE \"{database}\"");
        return await ConnectionStringAsync(database);
    }

    /// <summary>
    /// Creates a database with every sample migration applied and returns its connection string.
    /// PostgreSQL copies it from a template database that the first call migrates, so the sample
    /// migrations run once per test run instead of once per host.
    /// </summary>
    public async Task<string> CreateMigratedDatabaseAsync(string name)
    {
        var template = await _migratedTemplate.Value;
        var database = NewDatabaseName(name);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await ExecuteAsync($"CREATE DATABASE \"{database}\" TEMPLATE \"{template}\"");
                return await ConnectionStringAsync(database);
            }
            catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.ObjectInUse
                && attempt < TemplateCopyAttempts)
            {
                // PostgreSQL copies a template only while nobody is connected to it. It waits up to
                // five seconds for connections that are still closing; try again if one outlasts that.
                await Task.Delay(TimeSpan.FromMilliseconds(250 * attempt));
            }
        }
    }

    /// <summary>
    /// Migrates the template database once. Afterwards nothing may connect to it: the migration's
    /// pooled connections are closed and the database refuses new ones.
    /// </summary>
    private async Task<string> CreateMigratedTemplateAsync()
    {
        var template = NewDatabaseName("sample_template");
        await ExecuteAsync($"CREATE DATABASE \"{template}\"");
        var connectionString = await ConnectionStringAsync(template);

        var options = new DbContextOptionsBuilder<SampleProjectManagementDbContext>()
            .UseNpgsql(connectionString)
            .Options;
        await using (var dbContext = new SampleProjectManagementDbContext(options))
        {
            await dbContext.Database.MigrateAsync();
        }

        await using (var connection = new NpgsqlConnection(connectionString))
        {
            NpgsqlConnection.ClearPool(connection);
        }
        await ExecuteAsync($"ALTER DATABASE \"{template}\" WITH ALLOW_CONNECTIONS false");
        return template;
    }

    private string NewDatabaseName(string name)
    {
        return $"{name}_{Interlocked.Increment(ref _databaseCount):D3}";
    }

    private async Task ExecuteAsync(string sql)
    {
        var container = await _container.Value;
        await using var connection = new NpgsqlConnection(container.GetConnectionString());
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<string> ConnectionStringAsync(string database)
    {
        var container = await _container.Value;
        return new NpgsqlConnectionStringBuilder(container.GetConnectionString()) { Database = database }.ConnectionString;
    }

    public async ValueTask DisposeAsync()
    {
        if (_container.IsValueCreated && _container.Value.IsCompletedSuccessfully)
        {
            await _container.Value.Result.DisposeAsync();
        }
    }

    private static async Task<PostgreSqlContainer> StartAsync()
    {
        var container = new PostgreSqlBuilder("postgres:16-alpine")
            // Test classes run in parallel and every database has connection pools of its own.
            .WithCommand("-c", "max_connections=300")
            .Build();
        try
        {
            await container.StartAsync();
            return container;
        }
        catch
        {
            await container.DisposeAsync();
            throw;
        }
    }
}
