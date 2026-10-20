using System.Collections.Concurrent;
using DotNet.Testcontainers.Containers;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.MsSql;
using Testcontainers.PostgreSql;

namespace NewHeap.Platform.AI.Chat.Tests.Infrastructure;

/// <summary>
/// One real database server per provider for the whole test process, started when a test first
/// uses the provider, with a pool of migrated assistant schemas.
/// </summary>
/// <remarks>
/// A host leases a schema, which is emptied before it is handed out, and returns it when the host is
/// disposed. The pool only migrates a new schema when every schema is in use, so the checked-in
/// migrations run once per concurrently used schema instead of once per test. The servers are never
/// stopped explicitly: the Testcontainers resource reaper removes them when the process exits.
/// </remarks>
internal sealed class AssistantDatabaseServer
{
    private const string SqlServerImage = "mcr.microsoft.com/mssql/server:2022-CU14-ubuntu-22.04";
    private const string PostgreSqlImage = "postgres:16-alpine";

    private static readonly AssistantDatabaseServer SqlServer = new(AssistantTestProvider.SqlServer);
    private static readonly AssistantDatabaseServer PostgreSql = new(AssistantTestProvider.PostgreSql);

    private readonly AssistantTestProvider _provider;
    private readonly Lazy<Task<string>> _connectionString;
    private readonly ConcurrentQueue<AssistantSchemaSlot> _freeSlots = new();

    /// <summary>
    /// <c>CREATE DATABASE</c> copies the <c>model</c> database, so SQL Server creates one pool
    /// database at a time.
    /// </summary>
    private readonly SemaphoreSlim _databaseCreation = new(1, 1);

    /// <summary>
    /// The started container, referenced for the life of the process; the resource reaper stops it.
    /// </summary>
    private IContainer? _container;

    private int _slotCount;

    private AssistantDatabaseServer(AssistantTestProvider provider)
    {
        _provider = provider;
        _connectionString = new Lazy<Task<string>>(StartAsync, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public static AssistantDatabaseServer For(AssistantTestProvider provider)
    {
        return provider == AssistantTestProvider.SqlServer ? SqlServer : PostgreSql;
    }

    /// <summary>
    /// The connection string of the server's default database, which starts the server on first use.
    /// </summary>
    public Task<string> GetConnectionStringAsync()
    {
        return _connectionString.Value;
    }

    /// <summary>
    /// Leases a free schema after emptying it, or migrates a new one when every schema is in use.
    /// Emptying at lease time also removes rows that a previous host wrote after it returned the
    /// schema.
    /// </summary>
    public async Task<AssistantSchemaLease> LeaseAsync()
    {
        if (_freeSlots.TryDequeue(out var free))
        {
            await AssistantSchemaReset.ResetAsync(_provider, free.ConnectionString, free.Schema);
            return new AssistantSchemaLease(this, _provider, free);
        }

        return new AssistantSchemaLease(this, _provider, await MigrateNewSlotAsync());
    }

    /// <summary>
    /// Makes a schema available again. The schema that waited longest is leased first, which gives
    /// late writes of a disposed host the most time to finish before the schema is emptied.
    /// </summary>
    internal void Return(AssistantSchemaSlot slot)
    {
        _freeSlots.Enqueue(slot);
    }

    /// <summary>
    /// Migrates a new schema. On SQL Server every schema gets a database of its own: EF Core holds an
    /// application lock on the whole database while it migrates, so schemas that shared a database
    /// would migrate one after the other. PostgreSQL locks only the history table of the schema, so
    /// its schemas share the default database and still migrate in parallel.
    /// </summary>
    private async Task<AssistantSchemaSlot> MigrateNewSlotAsync()
    {
        var serverConnectionString = await GetConnectionStringAsync();
        var name = "pool" + Interlocked.Increment(ref _slotCount).ToString("D3");
        var slot = _provider == AssistantTestProvider.SqlServer
            ? new AssistantSchemaSlot(await CreateSqlServerDatabaseAsync(serverConnectionString, "assistant_" + name), name)
            : new AssistantSchemaSlot(serverConnectionString, name);

        await using var services = AssistantDatabaseFixture
            .CreateStorageServices(_provider, slot.ConnectionString, slot.Schema, null)
            .BuildServiceProvider();
        await AssistantDatabaseFixture.MigrateAsync(services);
        return slot;
    }

    /// <summary>
    /// Creates an empty pool database. Its commits do not wait for the log flush (delayed durability),
    /// as the PostgreSQL container runs without <c>fsync</c> and synchronous commit: that only matters
    /// when the server crashes, which ends the test run anyway. Transactions, locks and isolation
    /// behave as without it.
    /// </summary>
    private async Task<string> CreateSqlServerDatabaseAsync(string serverConnectionString, string database)
    {
        await _databaseCreation.WaitAsync();
        try
        {
            await using var connection = new SqlConnection(serverConnectionString);
            await connection.OpenAsync();
            await using var create = new SqlCommand(
                """
                DECLARE @statements nvarchar(max) =
                    N'CREATE DATABASE ' + QUOTENAME(@database) + N'; '
                    + N'ALTER DATABASE ' + QUOTENAME(@database) + N' SET DELAYED_DURABILITY = FORCED;';
                EXEC sys.sp_executesql @statements;
                """,
                connection);
            create.Parameters.AddWithValue("@database", database);
            await create.ExecuteNonQueryAsync();
        }
        finally
        {
            _databaseCreation.Release();
        }

        return new SqlConnectionStringBuilder(serverConnectionString) { InitialCatalog = database }.ConnectionString;
    }

    private async Task<string> StartAsync()
    {
        if (_provider == AssistantTestProvider.SqlServer)
        {
            var sqlServer = new MsSqlBuilder(SqlServerImage).Build();
            _container = sqlServer;
            await sqlServer.StartAsync();
            return sqlServer.GetConnectionString();
        }

        var postgreSql = new PostgreSqlBuilder(PostgreSqlImage)
            // Test classes run in parallel and each host has its own connections; the default of 100
            // connections is too tight for that.
            .WithCommand("-c", "max_connections=300")
            .Build();
        _container = postgreSql;
        await postgreSql.StartAsync();
        return postgreSql.GetConnectionString();
    }
}

/// <summary>
/// Where a pooled assistant schema lives: the connection string of its database and its name.
/// </summary>
internal sealed record AssistantSchemaSlot(string ConnectionString, string Schema);

/// <summary>
/// A migrated, empty assistant schema that one host uses exclusively. Disposing the lease returns the
/// schema to the pool, which empties it again before the next lease.
/// </summary>
internal sealed class AssistantSchemaLease : IAsyncDisposable, IDisposable
{
    private readonly AssistantDatabaseServer _server;
    private readonly AssistantSchemaSlot _slot;
    private int _returned;

    internal AssistantSchemaLease(
        AssistantDatabaseServer server,
        AssistantTestProvider provider,
        AssistantSchemaSlot slot)
    {
        _server = server;
        _slot = slot;
        Provider = provider;
    }

    public AssistantTestProvider Provider { get; }

    public string ConnectionString => _slot.ConnectionString;

    public string Schema => _slot.Schema;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _returned, 1) == 0)
        {
            _server.Return(_slot);
        }
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}
