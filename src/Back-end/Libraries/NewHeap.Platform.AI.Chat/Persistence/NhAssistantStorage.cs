using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace NewHeap.Platform.AI.Chat.Persistence;

/// <summary>
/// Creates short-lived assistant contexts. Every component uses its own context so the turn
/// runner, the durable managers and the SSE writer never share a change tracker.
/// </summary>
internal sealed class NhAssistantDbContextFactory
{
    private readonly Lazy<DbContextOptions<NhAssistantDbContext>> _options;
    private readonly NhAssistantDbContextOptions _storageOptions;

    public NhAssistantDbContextFactory(
        IServiceProvider services,
        NhAssistantStorageRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(registration);
        _storageOptions = registration.StorageOptions;
        _options = new Lazy<DbContextOptions<NhAssistantDbContext>>(
            () => CreateOptions(services, registration),
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public NhAssistantDbContext CreateDbContext()
    {
        return new NhAssistantDbContext(_options.Value, _storageOptions);
    }

    private static DbContextOptions<NhAssistantDbContext> CreateOptions(
        IServiceProvider services,
        NhAssistantStorageRegistration registration)
    {
        var builder = new DbContextOptionsBuilder<NhAssistantDbContext>();
        var loggerFactory = services.GetService<ILoggerFactory>();
        if (loggerFactory is not null)
        {
            builder.UseLoggerFactory(loggerFactory);
        }
        builder.ReplaceService<IModelCacheKeyFactory, NhAssistantModelCacheKeyFactory>();
        if (!string.Equals(
            registration.StorageOptions.Schema,
            NhAssistantDbContextOptions.DefaultSchema,
            StringComparison.Ordinal))
        {
            // The checked-in snapshot uses the default schema; a custom schema is an intentional model difference.
            builder.ConfigureWarnings(warnings =>
                warnings.Ignore(RelationalEventId.PendingModelChangesWarning));
        }
        registration.ConfigureProvider(services, builder);
        return builder.Options;
    }
}

/// <summary>
/// Provider registration captured by <c>UseSqlServer</c> or <c>UsePostgreSql</c>.
/// </summary>
internal sealed class NhAssistantStorageRegistration(
    string providerName,
    NhAssistantDbContextOptions storageOptions,
    Action<IServiceProvider, DbContextOptionsBuilder> configureProvider)
{
    public string ProviderName { get; } = providerName;

    public NhAssistantDbContextOptions StorageOptions { get; } = storageOptions;

    public Action<IServiceProvider, DbContextOptionsBuilder> ConfigureProvider { get; } = configureProvider;
}

internal static class NhAssistantStorage
{
    public static void Register(
        IServiceCollection services,
        NhAssistantStorageRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(registration);
        ValidateSchema(registration.StorageOptions.Schema);

        var existing = services
            .Where(descriptor => descriptor.ServiceType == typeof(NhAssistantStorageRegistration))
            .Select(descriptor => descriptor.ImplementationInstance)
            .OfType<NhAssistantStorageRegistration>()
            .SingleOrDefault();
        if (existing is not null)
        {
            if (!string.Equals(existing.ProviderName, registration.ProviderName, StringComparison.Ordinal)
                || !string.Equals(
                    existing.StorageOptions.Schema,
                    registration.StorageOptions.Schema,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "The assistant storage is already registered with a different provider or schema.");
            }
            return;
        }

        services.AddSingleton(registration);
        services.AddSingleton(registration.StorageOptions);
        services.TryAddSingleton<NhAssistantDbContextFactory>();
        services.TryAddScoped(provider =>
            provider.GetRequiredService<NhAssistantDbContextFactory>().CreateDbContext());
        if (registration.StorageOptions.RunMigrations)
        {
            services.TryAddEnumerable(
                ServiceDescriptor.Singleton<IHostedService, NhAssistantMigrationHostedService>());
        }
    }

    internal static void ValidateSchema(string schema)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(schema);
        if (schema.Length > 64
            || !schema.All(character => char.IsAsciiLetterOrDigit(character) || character == '_'))
        {
            throw new ArgumentException(
                "The assistant schema must contain only ASCII letters, digits and underscores.",
                nameof(schema));
        }
    }
}

/// <summary>
/// Applies library migrations during host start when <see cref="NhAssistantDbContextOptions.RunMigrations"/> is enabled.
/// Start blocks until the schema is current, so requests never reach an unmigrated store.
/// </summary>
internal sealed class NhAssistantMigrationHostedService(
    NhAssistantDbContextFactory contextFactory,
    ILogger<NhAssistantMigrationHostedService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await using var context = contextFactory.CreateDbContext();
        logger.LogInformation("Applying NewHeap assistant storage migrations.");
        await context.Database.MigrateAsync(cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}
