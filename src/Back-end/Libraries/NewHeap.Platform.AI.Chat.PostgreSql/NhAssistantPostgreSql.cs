using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using NewHeap.Platform.AI.Chat.Persistence;
using Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure.Internal;
using Npgsql.EntityFrameworkCore.PostgreSQL.Migrations;

namespace NewHeap.Platform.AI.Chat;

/// <summary>
/// Registers PostgreSQL storage for the assistant.
/// </summary>
public static class NhAssistantPostgreSqlBuilderExtensions
{
    public const string ProviderName = "postgresql";

    /// <summary>
    /// Stores conversations, approvals and durable ledgers in PostgreSQL.
    /// </summary>
    public static NhAssistantBuilder UsePostgreSql(
        this NhAssistantBuilder builder,
        string connectionString,
        Action<NhAssistantDbContextOptions>? configure = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        return builder.UsePostgreSql(_ => connectionString, configure);
    }

    /// <summary>
    /// Stores conversations, approvals and durable ledgers in PostgreSQL with a connection string
    /// resolved from the application services, for example from configuration.
    /// </summary>
    public static NhAssistantBuilder UsePostgreSql(
        this NhAssistantBuilder builder,
        Func<IServiceProvider, string> connectionStringFactory,
        Action<NhAssistantDbContextOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(connectionStringFactory);
        return builder.UseStorage(NhAssistantPostgreSqlStorage.CreateRegistration(
            connectionStringFactory,
            configure));
    }
}

internal static class NhAssistantPostgreSqlStorage
{
    public static NhAssistantStorageRegistration CreateRegistration(
        Func<IServiceProvider, string> connectionStringFactory,
        Action<NhAssistantDbContextOptions>? configure)
    {
        var options = new NhAssistantDbContextOptions();
        configure?.Invoke(options);
        NhAssistantStorage.ValidateSchema(options.Schema);
        return new NhAssistantStorageRegistration(
            NhAssistantPostgreSqlBuilderExtensions.ProviderName,
            options,
            (services, builder) =>
            {
                builder.ReplaceService<IMigrationsSqlGenerator, NhAssistantPostgreSqlMigrationsSqlGenerator>();
                builder.UseNpgsql(connectionStringFactory(services), postgreSql =>
                {
                    postgreSql.MigrationsAssembly(typeof(NhAssistantPostgreSqlStorage).Assembly.FullName);
                    postgreSql.MigrationsHistoryTable("__EFMigrationsHistory", options.Schema);
                });
            });
    }
}

#pragma warning disable EF1001 // The Npgsql SQL generator constructor requires its provider options.
internal sealed class NhAssistantPostgreSqlMigrationsSqlGenerator(
    MigrationsSqlGeneratorDependencies dependencies,
    INpgsqlSingletonOptions npgsqlOptions)
    : NpgsqlMigrationsSqlGenerator(dependencies, npgsqlOptions)
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

internal sealed class NhAssistantPostgreSqlDesignTimeDbContextFactory :
    IDesignTimeDbContextFactory<NhAssistantDbContext>
{
    public NhAssistantDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("NH_ASSISTANT_POSTGRESQL_CONNECTION")
            ?? "Host=localhost;Database=nh_assistant;Username=postgres;Password=postgres";
        var storageOptions = new NhAssistantDbContextOptions();
        var options = new DbContextOptionsBuilder<NhAssistantDbContext>()
            .UseNpgsql(connectionString, postgreSql =>
            {
                postgreSql.MigrationsAssembly(typeof(NhAssistantPostgreSqlDesignTimeDbContextFactory).Assembly.FullName);
                postgreSql.MigrationsHistoryTable("__EFMigrationsHistory", storageOptions.Schema);
            })
            .Options;
        return new NhAssistantDbContext(options, storageOptions);
    }
}
