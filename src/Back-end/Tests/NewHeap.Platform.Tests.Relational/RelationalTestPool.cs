using System.Collections.Concurrent;

namespace NewHeap.Platform.Tests.Relational;

/// <summary>
/// Where the slots of a <see cref="RelationalTestPool"/> live.
/// </summary>
public enum RelationalTestPoolLayout
{
    /// <summary>
    /// Every slot is a schema of its own, for a model whose schema is configurable, such as a
    /// library's storage. PostgreSQL keeps the schemas in the server's default database. SQL Server
    /// gives every schema a database of its own: EF Core holds an application lock on the whole
    /// database while it migrates, so schemas that shared a database would migrate one after the
    /// other, while PostgreSQL locks only the history table of the schema.
    /// </summary>
    SchemaPerSlot = 0,

    /// <summary>
    /// Every slot is a database of its own and the model decides its schemas, for an application's
    /// model or for a test that needs a database other than the one of another pool.
    /// </summary>
    DatabasePerSlot = 1
}

/// <summary>
/// A prepared slot of a pool: the connection string of its database and the schema that its tables
/// use by default.
/// </summary>
public sealed record RelationalTestSlot(RelationalTestProvider Provider, string ConnectionString, string Schema);

/// <summary>
/// A pool of prepared, empty databases or schemas of one model on the provider's
/// <see cref="RelationalTestServer"/>. A test leases a slot and returns it when it is done, so the
/// model is created once per slot that is in use at the same time instead of once per test.
/// </summary>
/// <remarks>
/// A slot is emptied when it is leased, not when it is returned, so late writes of the previous
/// user never reach the next test. Emptying deletes every row of every table of the slot, read from
/// the database catalog, and keeps the EF Core migration history, so a new migration or table needs
/// no change here. PostgreSQL also restarts its sequences; SQL Server identity columns continue
/// counting, so pool a model with identity columns only for tests that do not depend on generated
/// values. The free slot that waited longest is leased first.
/// </remarks>
public sealed class RelationalTestPool
{
    private readonly RelationalTestServer _server;
    private readonly string _name;
    private readonly RelationalTestPoolLayout _layout;
    private readonly Func<RelationalTestSlot, Task> _prepareAsync;
    private readonly ConcurrentQueue<RelationalTestSlot> _freeSlots = new();
    private int _slotCount;

    /// <param name="provider">The provider whose shared server holds the slots.</param>
    /// <param name="name">
    /// Names the pool's databases or schemas: lowercase letters, digits and underscores, starting
    /// with a letter. Pools of one process need different names.
    /// </param>
    /// <param name="layout">Whether a slot is a schema or a database of its own.</param>
    /// <param name="prepareAsync">
    /// Creates the model in a new, empty slot, for example by applying its migrations. It runs once
    /// per slot.
    /// </param>
    public RelationalTestPool(
        RelationalTestProvider provider,
        string name,
        RelationalTestPoolLayout layout,
        Func<RelationalTestSlot, Task> prepareAsync)
    {
        RelationalTestServer.ValidateName(name, nameof(name));
        _server = RelationalTestServer.For(provider);
        _name = name;
        _layout = layout;
        _prepareAsync = prepareAsync;
    }

    public RelationalTestProvider Provider => _server.Provider;

    /// <summary>
    /// Leases a free slot after emptying it, or prepares a new one when every slot is in use.
    /// </summary>
    public async Task<RelationalTestLease> LeaseAsync()
    {
        if (_freeSlots.TryDequeue(out var free))
        {
            var schema = _layout == RelationalTestPoolLayout.SchemaPerSlot ? free.Schema : null;
            await RelationalTestReset.ResetAsync(free.Provider, free.ConnectionString, schema);
            return new RelationalTestLease(this, free);
        }

        var slot = await CreateSlotAsync();
        await _prepareAsync(slot);
        return new RelationalTestLease(this, slot);
    }

    internal void Return(RelationalTestSlot slot)
    {
        _freeSlots.Enqueue(slot);
    }

    private async Task<RelationalTestSlot> CreateSlotAsync()
    {
        if (_layout == RelationalTestPoolLayout.DatabasePerSlot)
        {
            var database = await _server.CreateDatabaseAsync(_name);
            var defaultSchema = Provider == RelationalTestProvider.SqlServer ? "dbo" : "public";
            return new RelationalTestSlot(Provider, database, defaultSchema);
        }

        var schema = _name + "_" + Interlocked.Increment(ref _slotCount).ToString("D3");
        var connectionString = Provider == RelationalTestProvider.SqlServer
            ? await _server.CreateDatabaseAsync(_name)
            : await _server.GetConnectionStringAsync();
        return new RelationalTestSlot(Provider, connectionString, schema);
    }
}

/// <summary>
/// A prepared, empty slot that one test uses exclusively. Disposing the lease returns the slot to
/// its pool, which empties it again before the next lease.
/// </summary>
public sealed class RelationalTestLease : IAsyncDisposable, IDisposable
{
    private readonly RelationalTestPool _pool;
    private readonly RelationalTestSlot _slot;
    private int _returned;

    internal RelationalTestLease(RelationalTestPool pool, RelationalTestSlot slot)
    {
        _pool = pool;
        _slot = slot;
    }

    public RelationalTestProvider Provider => _slot.Provider;

    public string ConnectionString => _slot.ConnectionString;

    /// <summary>
    /// The slot's schema; for a database of its own, the provider's default schema.
    /// </summary>
    public string Schema => _slot.Schema;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _returned, 1) == 0)
        {
            _pool.Return(_slot);
        }
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}
