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
            });
    }
}

#pragma warning disable EF1001 // The SQL Server generator constructor is part of the provider extension surface.
internal sealed class NhAssistantSqlServerMigrationsSqlGenerator(
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
        NhAssistantMigrationSchema.Apply(
            operations,
            NhAssistantMigrationSchema.GetConfiguredSchema(Dependencies.CurrentContext.Context));
        return base.Generate(operations, model, options);
    }
}
#pragma warning restore EF1001

internal sealed class NhAssistantSqlServerDesignTimeDbContextFactory :
    IDesignTimeDbContextFactory<NhAssistantDbContext>
{
    public NhAssistantDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("NH_ASSISTANT_SQLSERVER_CONNECTION")
            ?? "Server=localhost;Database=nh_assistant;Trusted_Connection=True;TrustServerCertificate=True";
        var storageOptions = new NhAssistantDbContextOptions();
        var options = new DbContextOptionsBuilder<NhAssistantDbContext>()
            .UseSqlServer(connectionString, sqlServer =>
            {
                sqlServer.MigrationsAssembly(typeof(NhAssistantSqlServerDesignTimeDbContextFactory).Assembly.FullName);
                sqlServer.MigrationsHistoryTable("__EFMigrationsHistory", storageOptions.Schema);
            })
            .Options;
        return new NhAssistantDbContext(options, storageOptions);
    }
}
