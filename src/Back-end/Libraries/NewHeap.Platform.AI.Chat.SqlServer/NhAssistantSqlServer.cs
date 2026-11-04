using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.EntityFrameworkCore.Update;
using NewHeap.Platform.AI.Chat.Persistence;

namespace NewHeap.Platform.AI.Chat;

/// <summary>
/// Registers SQL Server storage for the assistant.
/// </summary>
public static class NhAssistantSqlServerBuilderExtensions
{
    public const string ProviderName = "sql-server";

    /// <summary>
    /// Stores conversations, approvals and durable ledgers in SQL Server.
    /// </summary>
    public static NhAssistantBuilder UseSqlServer(
        this NhAssistantBuilder builder,
        string connectionString,
        Action<NhAssistantDbContextOptions>? configure = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        return builder.UseSqlServer(_ => connectionString, configure);
    }

    /// <summary>
    /// Stores conversations, approvals and durable ledgers in SQL Server with a connection string
    /// resolved from the application services, for example from configuration.
    /// </summary>
    public static NhAssistantBuilder UseSqlServer(
        this NhAssistantBuilder builder,
        Func<IServiceProvider, string> connectionStringFactory,
        Action<NhAssistantDbContextOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(connectionStringFactory);
        return builder.UseStorage(NhAssistantSqlServerStorage.CreateRegistration(
            connectionStringFactory,
            configure));
    }
}

internal static class NhAssistantSqlServerStorage
{
    public static NhAssistantStorageRegistration CreateRegistration(
        Func<IServiceProvider, string> connectionStringFactory,
        Action<NhAssistantDbContextOptions>? configure)
    {
        var options = new NhAssistantDbContextOptions();
        configure?.Invoke(options);
        NhAssistantStorage.ValidateSchema(options.Schema);
        return new NhAssistantStorageRegistration(
            NhAssistantSqlServerBuilderExtensions.ProviderName,
            options,
            (services, builder) =>
            {
                builder.ReplaceService<IMigrationsSqlGenerator, NhAssistantSqlServerMigrationsSqlGenerator>();
                builder.UseSqlServer(connectionStringFactory(services), sqlServer =>
                {
                    sqlServer.MigrationsAssembly(typeof(NhAssistantSqlServerStorage).Assembly.FullName);
                    sqlServer.MigrationsHistoryTable("__EFMigrationsHistory", options.Schema);
                });
            },
            new NhAssistantSqlServerTransactionLocks());
    }
}

#pragma warning disable EF1001 // The SQL Server generator constructor is part of the provider extension surface.
internal sealed partial class NhAssistantSqlServerMigrationsSqlGenerator(
    MigrationsSqlGeneratorDependencies dependencies,
    ICommandBatchPreparer commandBatchPreparer)
    : SqlServerMigrationsSqlGenerator(dependencies, commandBatchPreparer)
{
    public override IReadOnlyList<MigrationCommand> Generate(
        IReadOnlyList<MigrationOperation> operations,
        IModel? model = null,
        MigrationsSqlGenerationOptions options = MigrationsSqlGenerationOptions.Default)
    {
        // Checked-in migrations target the default schema; route them without rewriting history.
        var schema = NhAssistantMigrationSchema.GetConfiguredSchema(Dependencies.CurrentContext.Context);
        NhAssistantMigrationSchema.Apply(operations, schema);
        NhAssistantMigrationSchema.ApplyToSql(operations, Dependencies.SqlGenerationHelper.DelimitIdentifier(schema));
        return base.Generate(
            options.HasFlag(MigrationsSqlGenerationOptions.Idempotent) ? operations.Select(CompiledOnlyWhenItRuns).ToArray() : operations,
            model,
            options);
    }

    /// <summary>
    /// The SQL of a migration as an idempotent script must run it. SQL Server compiles a whole batch before it runs
    /// any of it, also what the script's check of the migration history skips, and it defers the names of missing
    /// tables but not of missing columns. So a data move that reads columns a later operation drops stops the script
    /// on every database past that migration (Msg 207). In <c>EXEC</c> it compiles only when the migration runs.
    /// A new operation leaves the migration's own, which later generations of the script read again, as it is.
    /// </summary>
    private static MigrationOperation CompiledOnlyWhenItRuns(MigrationOperation operation)
    {
        // EXEC holds one batch: SQL with its own batches is the provider's to split, and is left to it.
        if (operation is not SqlOperation sql || BatchSeparator().IsMatch(sql.Sql))
        {
            return operation;
        }

        return new SqlOperation
        {
            Sql = $"EXEC(N'{sql.Sql.Replace("'", "''", StringComparison.Ordinal)}');",
            IsDestructiveChange = sql.IsDestructiveChange,
            SuppressTransaction = sql.SuppressTransaction
        };
    }

    /// <summary>A line of its own with the batch separator <c>GO</c>, as the provider's generator splits on it.</summary>
    [GeneratedRegex(@"^\s*GO(?:[ \t]+[0-9]+)?\s*$", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex BatchSeparator();
}
#pragma warning restore EF1001

/// <summary>
/// The context the EF tools create to add migrations and to write scripts. It generates SQL as a host
/// does, with the schema routing of <see cref="NhAssistantSqlServerMigrationsSqlGenerator"/>, so a script
/// carries no schema placeholder; <c>-- --schema &lt;name&gt;</c> writes a script for another schema
/// (<see cref="NhAssistantStorage.DesignTimeOptions"/>).
/// </summary>
internal sealed class NhAssistantSqlServerDesignTimeDbContextFactory :
    IDesignTimeDbContextFactory<NhAssistantDbContext>
{
    public NhAssistantDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("NH_ASSISTANT_SQLSERVER_CONNECTION")
            ?? "Server=localhost;Database=nh_assistant;Trusted_Connection=True;TrustServerCertificate=True";
        var storageOptions = NhAssistantStorage.DesignTimeOptions(args);
        var options = new DbContextOptionsBuilder<NhAssistantDbContext>()
            .ReplaceService<IMigrationsSqlGenerator, NhAssistantSqlServerMigrationsSqlGenerator>()
            .UseSqlServer(connectionString, sqlServer =>
            {
                sqlServer.MigrationsAssembly(typeof(NhAssistantSqlServerDesignTimeDbContextFactory).Assembly.FullName);
                sqlServer.MigrationsHistoryTable("__EFMigrationsHistory", storageOptions.Schema);
            })
            .Options;
        return new NhAssistantDbContext(options, storageOptions);
    }
}
