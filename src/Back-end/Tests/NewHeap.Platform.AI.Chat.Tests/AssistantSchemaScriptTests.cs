using System.Data.Common;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.AI.Chat.Persistence;
using NewHeap.Platform.AI.Chat.Tests.Infrastructure;
using NewHeap.Platform.Tests.Relational;
using Npgsql;
using Xunit;

namespace NewHeap.Platform.AI.Chat.Tests;

/// <summary>
/// The idempotent SQL scripts that a release publishes for the assistant schema (FB92) come from the
/// provider projects' design-time factories, as <c>dotnet ef migrations script --idempotent</c> writes
/// them: they route the checked-in migrations as a host does, so no schema placeholder remains, and
/// <c>-- --schema &lt;name&gt;</c> writes the script of another schema. Applied to an empty database the
/// script creates the complete schema, and applied again it changes nothing, on both providers.
/// </summary>
public sealed partial class AssistantSchemaScriptTests(AssistantDatabaseFixture database) : IClassFixture<AssistantDatabaseFixture>
{
    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public void The_design_time_script_routes_the_schema_without_placeholders(AssistantTestProvider provider)
    {
        var (defaultScript, defaultSchema) = ScriptOf(provider, []);
        var (routedScript, routedSchema) = ScriptOf(provider, ["--schema", AssistantDatabaseFixture.UniqueSchema()]);

        Assert.DoesNotContain(NhAssistantMigrationSchema.SchemaPlaceholder, defaultScript, StringComparison.Ordinal);
        Assert.Contains(defaultSchema + ".", defaultScript, StringComparison.Ordinal);
        Assert.DoesNotContain(NhAssistantMigrationSchema.SchemaPlaceholder, routedScript, StringComparison.Ordinal);
        Assert.Contains(routedSchema + ".", routedScript, StringComparison.Ordinal);
        Assert.DoesNotContain(defaultSchema + ".", routedScript, StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => ScriptOf(provider, ["--schema", "nhai; drop"]));
        Assert.Throws<ArgumentException>(() => ScriptOf(provider, ["--schema"]));
    }

    [Theory]
    [InlineData(AssistantTestProvider.SqlServer)]
    [InlineData(AssistantTestProvider.PostgreSql)]
    public async Task The_idempotent_script_creates_the_schema_in_an_empty_database_and_changes_nothing_when_run_again(
        AssistantTestProvider provider)
    {
        var connectionString = await RelationalTestServer.For(AssistantDatabaseFixture.ToRelational(provider))
            .CreateDatabaseAsync("assistant_script");
        var (script, _) = ScriptOf(provider, []);

        await ApplyAsync(provider, connectionString, script);
        await ApplyAsync(provider, connectionString, script);

        await using var services = AssistantDatabaseFixture
            .CreateStorageServices(provider, connectionString, NhAssistantDbContextOptions.DefaultSchema, null)
            .BuildServiceProvider();
        await using var context = services.GetRequiredService<NhAssistantDbContextFactory>().CreateDbContext();
        Assert.Empty(await context.Database.GetPendingMigrationsAsync());
        Assert.Equal(context.Database.GetMigrations(), await context.Database.GetAppliedMigrationsAsync());
        Assert.Equal(0, await context.Conversations.CountAsync());
        Assert.Equal(0, await context.McpReaders.CountAsync());
        Assert.Equal(0, await context.PushSubscriptions.CountAsync());
    }

    /// <summary>
    /// The idempotent script from an empty database that the provider's design-time factory writes with
    /// the arguments the EF tools pass after <c>--</c>, and its schema as the provider delimits it.
    /// </summary>
    private static (string Script, string Schema) ScriptOf(AssistantTestProvider provider, string[] args)
    {
        using var context = provider == AssistantTestProvider.SqlServer
            ? new NhAssistantSqlServerDesignTimeDbContextFactory().CreateDbContext(args)
            : new NhAssistantPostgreSqlDesignTimeDbContextFactory().CreateDbContext(args);
        return (
            context.GetService<IMigrator>().GenerateScript(options: MigrationsSqlGenerationOptions.Idempotent),
            context.GetService<ISqlGenerationHelper>().DelimitIdentifier(context.Schema));
    }

    /// <summary>
    /// Runs the script as a database tool does: SQL Server batch by batch between its <c>GO</c> lines,
    /// PostgreSQL as one command text.
    /// </summary>
    private static async Task ApplyAsync(AssistantTestProvider provider, string connectionString, string script)
    {
        await using DbConnection connection = provider == AssistantTestProvider.SqlServer
            ? new SqlConnection(connectionString)
            : new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        var batches = provider == AssistantTestProvider.SqlServer
            ? BatchSeparator().Split(script)
            : [script];
        foreach (var batch in batches.Where(batch => !string.IsNullOrWhiteSpace(batch)))
        {
            await using var command = connection.CreateCommand();
            command.CommandText = batch;
            command.CommandTimeout = (int)RelationalTestServer.PreparationCommandTimeout.TotalSeconds;
            await command.ExecuteNonQueryAsync();
        }
    }

    /// <summary>A line of its own with the SQL Server batch separator <c>GO</c>.</summary>
    [GeneratedRegex(@"^\s*GO\s*$", RegexOptions.Multiline)]
    private static partial Regex BatchSeparator();
}
