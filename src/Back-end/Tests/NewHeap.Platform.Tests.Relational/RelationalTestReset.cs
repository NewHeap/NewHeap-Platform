using Microsoft.Data.SqlClient;
using Npgsql;

namespace NewHeap.Platform.Tests.Relational;

/// <summary>
/// Empties a pooled slot so that it is as clean as a freshly prepared one: every row of every table
/// goes, the EF Core migration history stays. The tables are read from the database catalog, so a
/// table that a new migration adds is emptied without a change here.
/// </summary>
internal static class RelationalTestReset
{
    private const string HistoryTable = "__EFMigrationsHistory";
    private const int MaxAttempts = 3;

    /// <param name="schema">The schema to empty, or <see langword="null"/> for every table of the database.</param>
    public static async Task ResetAsync(RelationalTestProvider provider, string connectionString, string? schema)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                if (provider == RelationalTestProvider.SqlServer)
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
                // A write that the previous user started before it returned the slot can still hold
                // locks the reset needs; the database then picks one of the two as deadlock victim.
            }
        }
    }

    /// <summary>
    /// One <c>TRUNCATE</c> over every table; <c>CASCADE</c> covers the foreign keys between them and
    /// <c>RESTART IDENTITY</c> their sequences.
    /// </summary>
    private static async Task ResetPostgreSqlAsync(string connectionString, string? schema)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        var tables = new List<string>();
        await using (var list = new NpgsqlCommand(
            """
            SELECT schemaname, tablename
            FROM pg_tables
            WHERE schemaname NOT IN ('pg_catalog', 'information_schema')
                AND (@schema = '' OR schemaname = @schema)
                AND tablename <> @history
            """,
            connection))
        {
            list.Parameters.AddWithValue("schema", schema ?? string.Empty);
            list.Parameters.AddWithValue("history", HistoryTable);
            await using var reader = await list.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                tables.Add(QuotePostgreSql(reader.GetString(0)) + "." + QuotePostgreSql(reader.GetString(1)));
            }
        }

        if (tables.Count == 0)
        {
            return;
        }

        await using var truncate = new NpgsqlCommand(
            $"TRUNCATE TABLE {string.Join(", ", tables)} RESTART IDENTITY CASCADE",
            connection);
        truncate.CommandTimeout = RelationalTestServer.PreparationCommandTimeoutSeconds;
        await truncate.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// SQL Server cannot truncate a table that a foreign key references, so the rows are deleted
    /// table by table, every referencing table before the table it references.
    /// </summary>
    private static async Task ResetSqlServerAsync(string connectionString, string? schema)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();

        var tables = new List<string>();
        var references = new List<(string Referencing, string Referenced)>();
        await using (var list = new SqlCommand(
            """
            SELECT QUOTENAME(SCHEMA_NAME(t.schema_id)) + N'.' + QUOTENAME(t.name)
            FROM sys.tables t
            WHERE t.is_ms_shipped = 0
                AND (@schema = N'' OR t.schema_id = SCHEMA_ID(@schema))
                AND t.name <> @history;

            SELECT QUOTENAME(SCHEMA_NAME(referencing.schema_id)) + N'.' + QUOTENAME(referencing.name),
                QUOTENAME(SCHEMA_NAME(referenced.schema_id)) + N'.' + QUOTENAME(referenced.name)
            FROM sys.foreign_keys fk
            JOIN sys.tables referencing ON referencing.object_id = fk.parent_object_id
            JOIN sys.tables referenced ON referenced.object_id = fk.referenced_object_id
            WHERE fk.parent_object_id <> fk.referenced_object_id
                AND (@schema = N''
                    OR (referencing.schema_id = SCHEMA_ID(@schema) AND referenced.schema_id = SCHEMA_ID(@schema)));
            """,
            connection))
        {
            list.Parameters.AddWithValue("@schema", schema ?? string.Empty);
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

        var statements = DeleteOrder(tables, references).Select(table => $"DELETE FROM {table};");
        await using var delete = new SqlCommand(string.Join("\n", statements), connection);
        delete.CommandTimeout = RelationalTestServer.PreparationCommandTimeoutSeconds;
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
                    "The tables reference each other in a cycle; the pooled slot cannot be emptied table by table: "
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
}
