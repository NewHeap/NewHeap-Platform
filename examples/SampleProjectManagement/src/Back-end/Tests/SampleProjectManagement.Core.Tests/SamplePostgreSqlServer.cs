using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

[assembly: AssemblyFixture(typeof(SampleProjectManagement.Core.Tests.SamplePostgreSqlServer))]

namespace SampleProjectManagement.Core.Tests;

/// <summary>
/// One PostgreSQL container for the whole test run instead of one per test or sample host. Every
/// test that needs a real database uses a database of its own on it, so test classes run in
/// parallel without seeing each other's tables, rows or migrations: a sample host leases a migrated
/// sample database from <see cref="MigratedDatabases"/>, and a test that proves migrations creates
/// an empty database with <see cref="CreateDatabaseAsync"/>.
/// </summary>
/// <remarks>
/// xUnit creates the fixture once for the test assembly and passes it to every test class and class
/// fixture that asks for it in its constructor. The container starts when a test first creates a
/// database, so a run without database tests starts none, and stops when the run ends.
/// </remarks>
public sealed class SamplePostgreSqlServer : IAsyncDisposable
{
    private readonly Lazy<Task<PostgreSqlContainer>> _container = new(StartAsync);
    private int _databaseCount;

    public SamplePostgreSqlServer()
    {
        MigratedDatabases = new SampleDatabasePool(this);
    }

    /// <summary>
    /// The migrated sample databases that sample hosts lease, one host at a time per database.
    /// </summary>
    public SampleDatabasePool MigratedDatabases { get; }

    /// <summary>
    /// Creates an empty database and returns its connection string. The database name starts with
    /// <paramref name="name"/>, written in lowercase letters, digits and underscores.
    /// </summary>
    public async Task<string> CreateDatabaseAsync(string name)
    {
        var container = await _container.Value;
        var database = $"{name}_{Interlocked.Increment(ref _databaseCount):D3}";
        await using (var connection = new NpgsqlConnection(container.GetConnectionString()))
        {
            await connection.OpenAsync();
            await using var create = new NpgsqlCommand($"CREATE DATABASE \"{database}\"", connection);
            await create.ExecuteNonQueryAsync();
        }

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
