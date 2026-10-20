using Microsoft.Data.SqlClient;
using Npgsql;

namespace NewHeap.Platform.AI.Chat.Tests.Infrastructure;

/// <summary>
/// Empties a pooled assistant schema so that it is as clean as a freshly migrated one: every row of
/// every table goes, the migration history stays. The tables are read from the database catalog, so
/// a table that a new migration adds is emptied without a change here. The checked-in migrations
/// insert no rows, and no assistant table has an identity column.
/// </summary>
internal static class AssistantSchemaReset
{
    private const string HistoryTable = "__EFMigrationsHistory";
    private const int MaxAttempts = 3;

    public static async Task ResetAsync(AssistantTestProvider provider, string connectionString, string schema)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                if (provider == AssistantTestProvider.SqlServer)
                {
                    await ResetSqlServerAsync(connectionString, schema);
                }
                else
                {
                    await ResetPostgreSqlAsync(connectionString, schema);
                }

                return;
            }
            catch (Exception exception) when (attempt < MaxAttempts && IsDeadlock(exception))
            {
                // A write that the previous host started before it was disposed can still hold locks
                // the reset needs; the database then picks one of the two as deadlock victim.
            }
        }
    }

    /// <summary>
    /// One <c>TRUNCATE</c> over every table of the schema; <c>CASCADE</c> covers the foreign keys
    /// between them.
    /// </summary>
    private static async Task ResetPostgreSqlAsync(string connectionString, string schema)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        var tables = new List<string>();
        await using (var list = new NpgsqlCommand(
            "SELECT tablename FROM pg_tables WHERE schemaname = @schema AND tablename <> @history",
            connection))
        {
            list.Parameters.AddWithValue("schema", schema);
            list.Parameters.AddWithValue("history", HistoryTable);
            await using var reader = await list.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                tables.Add(QuotePostgreSql(schema) + "." + QuotePostgreSql(reader.GetString(0)));
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

    /// <summary>
    /// SQL Server cannot truncate a table that a foreign key references, so the rows are deleted
    /// table by table, every referencing table before the table it references.
    /// </summary>
    private static async Task ResetSqlServerAsync(string connectionString, string schema)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();

        var tables = new List<string>();
        var references = new List<(string Referencing, string Referenced)>();
        await using (var list = new SqlCommand(
            """
            SELECT t.name
            FROM sys.tables t
            WHERE t.schema_id = SCHEMA_ID(@schema) AND t.name <> @history;

            SELECT referencing.name, referenced.name
            FROM sys.foreign_keys fk
            JOIN sys.tables referencing ON referencing.object_id = fk.parent_object_id
            JOIN sys.tables referenced ON referenced.object_id = fk.referenced_object_id
            WHERE referencing.schema_id = SCHEMA_ID(@schema)
                AND referenced.schema_id = SCHEMA_ID(@schema)
                AND fk.parent_object_id <> fk.referenced_object_id;
            """,
            connection))
        {
            list.Parameters.AddWithValue("@schema", schema);
            list.Parameters.AddWithValue("@history", HistoryTable);
            await using var reader = await list.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                tables.Add(reader.GetString(0));
            }

            await reader.NextResultAsync();
            while (await reader.ReadAsync())
            {
                references.Add((reader.GetString(0), reader.GetString(1)));
            }
        }

        if (tables.Count == 0)
        {
            return;
        }

        var statements = DeleteOrder(tables, references)
            .Select(table => $"DELETE FROM {QuoteSqlServer(schema)}.{QuoteSqlServer(table)};");
        await using var delete = new SqlCommand(string.Join("\n", statements), connection);
        await delete.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Orders the tables so that no table is emptied while another remaining table still references
    /// it. A table that references itself empties in one statement and does not count.
    /// </summary>
    private static IReadOnlyList<string> DeleteOrder(
        IReadOnlyList<string> tables,
        IReadOnlyList<(string Referencing, string Referenced)> references)
    {
        var remaining = new HashSet<string>(tables, StringComparer.Ordinal);
        var ordered = new List<string>(tables.Count);
        while (remaining.Count > 0)
        {
            var unreferenced = remaining
                .Where(table => !references.Any(reference =>
                    reference.Referenced == table && remaining.Contains(reference.Referencing)))
                .Order(StringComparer.Ordinal)
                .ToList();
            if (unreferenced.Count == 0)
            {
                throw new InvalidOperationException(
                    "The assistant tables reference each other in a cycle; the pooled schema cannot be emptied table by table: "
                    + string.Join(", ", remaining.Order(StringComparer.Ordinal)));
            }

            ordered.AddRange(unreferenced);
            remaining.ExceptWith(unreferenced);
        }

        return ordered;
    }

    private static bool IsDeadlock(Exception exception)
    {
        return exception is SqlException { Number: 1205 }
            || exception is PostgresException { SqlState: PostgresErrorCodes.DeadlockDetected };
    }

    private static string QuotePostgreSql(string identifier)
    {
        return "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }

    private static string QuoteSqlServer(string identifier)
    {
        return "[" + identifier.Replace("]", "]]", StringComparison.Ordinal) + "]";
    }
}
