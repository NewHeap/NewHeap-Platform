using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using SampleProjectManagement.DAL;

namespace SampleProjectManagement.Core.Tests;

/// <summary>
/// A small pool of migrated sample databases on the shared <see cref="SamplePostgreSqlServer"/>. A
/// sample host leases a database, uses it alone and returns it when the host is disposed, so the
/// sample migrations run once per database instead of once per host.
/// </summary>
/// <remarks>
/// The pool grows lazily: a lease takes a database that an earlier host returned, or creates and
/// migrates a new one when every database is in use. A database is emptied when it is leased, not
/// when it is returned, so a late write of the previous host never reaches the next one. Emptying
/// keeps every EF Core migration history: a library that migrates its own schema when the host
/// starts, such as the assistant's <c>nhai</c> schema, finds its migrations applied and its tables
/// empty, as in a database of its own. A test that proves migrations, or a host whose application
/// creates its own schema, uses an empty database of its own from
/// <see cref="SamplePostgreSqlServer.CreateDatabaseAsync"/> instead.
/// </remarks>
public sealed class SampleDatabasePool(SamplePostgreSqlServer postgreSql)
{
    private const string HistoryTable = "__EFMigrationsHistory";

    private readonly ConcurrentQueue<string> _returnedDatabases = new();

    /// <summary>
    /// Leases a returned database after emptying it, or a new migrated database when every database
    /// is in use.
    /// </summary>
    public async Task<SampleDatabaseLease> LeaseAsync()
    {
        if (_returnedDatabases.TryDequeue(out var returned))
        {
            await EmptyAsync(returned);
            return new SampleDatabaseLease(this, returned);
        }

        var created = await postgreSql.CreateDatabaseAsync("sample_pool");
        await MigrateAsync(created);
        return new SampleDatabaseLease(this, created);
    }

    internal void Return(string connectionString)
    {
        _returnedDatabases.Enqueue(connectionString);
    }

    private static async Task MigrateAsync(string connectionString)
    {
        var options = new DbContextOptionsBuilder<SampleProjectManagementDbContext>()
            .UseNpgsql(connectionString)
            .Options;
        await using var dbContext = new SampleProjectManagementDbContext(options);
        await dbContext.Database.MigrateAsync();
    }

    /// <summary>
    /// One <c>TRUNCATE</c> over every table except the migration histories, read from the catalog so
    /// that a new migration needs no change here. <c>CASCADE</c> covers the foreign keys between the
    /// tables and <c>RESTART IDENTITY</c> their sequences.
    /// </summary>
    private static async Task EmptyAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        var tables = new List<string>();
        await using (var list = new NpgsqlCommand(
            """
            SELECT format('%I.%I', schemaname, tablename)
            FROM pg_tables
            WHERE schemaname NOT IN ('pg_catalog', 'information_schema')
                AND tablename <> @history
            """,
            connection))
        {
            list.Parameters.AddWithValue("history", HistoryTable);
            await using var reader = await list.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                tables.Add(reader.GetString(0));
            }
        }

        if (tables.Count == 0)
        {
            return;
        }

        await using var truncate = new NpgsqlCommand(
            $"TRUNCATE TABLE {string.Join(", ", tables)} RESTART IDENTITY CASCADE",
            connection);
        await truncate.ExecuteNonQueryAsync();
    }
}

/// <summary>
/// A migrated sample database that one host uses alone. Disposing the lease returns the database to
/// its pool, which empties it before the next lease.
/// </summary>
public sealed class SampleDatabaseLease : IAsyncDisposable
{
    private readonly SampleDatabasePool _pool;
    private int _returned;

    internal SampleDatabaseLease(SampleDatabasePool pool, string connectionString)
    {
        _pool = pool;
        ConnectionString = connectionString;
    }

    public string ConnectionString { get; }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _returned, 1) == 0)
        {
            _pool.Return(ConnectionString);
        }

        return ValueTask.CompletedTask;
    }
}
