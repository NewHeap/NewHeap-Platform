using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.Tests.Relational;

namespace NewHeap.Platform.AI.Chat.Tests.Infrastructure;

/// <summary>
/// The pools of migrated assistant schemas, one per provider, on the database servers that the test
/// process shares (see <see cref="RelationalTestServer"/>).
/// </summary>
/// <remarks>
/// A host leases a schema, which is emptied before it is handed out, and returns it when the host is
/// disposed. The pool only migrates a new schema when every schema is in use, so the checked-in
/// migrations run once per concurrently used schema instead of once per test. No assistant table has
/// an identity column, so emptying a SQL Server schema needs no reseed. See
/// <see cref="RelationalTestPool"/> for where the schemas live and how they are emptied.
/// </remarks>
internal static class AssistantSchemaPool
{
    private static readonly RelationalTestPool SqlServer = Create(AssistantTestProvider.SqlServer);
    private static readonly RelationalTestPool PostgreSql = Create(AssistantTestProvider.PostgreSql);

    public static Task<RelationalTestLease> LeaseAsync(AssistantTestProvider provider)
    {
        var pool = provider == AssistantTestProvider.SqlServer ? SqlServer : PostgreSql;
        return pool.LeaseAsync();
    }

    private static RelationalTestPool Create(AssistantTestProvider provider)
    {
        return new RelationalTestPool(
            AssistantDatabaseFixture.ToRelational(provider),
            "assistant",
            RelationalTestPoolLayout.SchemaPerSlot,
            slot => MigrateAsync(provider, slot));
    }

    private static async Task MigrateAsync(AssistantTestProvider provider, RelationalTestSlot slot)
    {
        await using var services = AssistantDatabaseFixture
            .CreateStorageServices(provider, slot.ConnectionString, slot.Schema, null)
            .BuildServiceProvider();
        await AssistantDatabaseFixture.MigrateAsync(services);
    }
}
