using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.AspNet.Common.DAL.Bulk;
using System.Data.Common;

namespace NewHeap.Platform.AspNet.Common.DAL;

internal abstract class NhRepositoryProvider
{
    internal abstract string ProviderName { get; }

    internal abstract Task<int> ExecuteUpsertAsync<TEntity>(
        DbContext context,
        BulkUpsertPlan<TEntity> plan,
        IEnumerable<TEntity> entities,
        DbTransaction transaction,
        bool hydrateMatchedPrimaryKeys,
        CancellationToken cancellationToken)
        where TEntity : class;

    internal abstract Task<bool> TryAcquireTransactionLockAsync(
        DbConnection connection,
        DbTransaction transaction,
        string resourceName,
        int lockTimeoutInMilliseconds,
        CancellationToken cancellationToken);

    internal static NhRepositoryProvider GetRequired(DbContext context)
    {
        var provider = context.GetService<IDbContextOptions>()
            .FindExtension<NhRepositoryProviderOptionsExtension>()?.Provider;
        if (provider is null || provider.ProviderName != context.Database.ProviderName)
        {
            throw new NotSupportedException(
                $"NewHeap repository operations are not configured for database provider '{context.Database.ProviderName ?? "unknown"}'. " +
                "Configure this DbContext with UseNewHeapSqlServer or UseNewHeapPostgreSql from the matching provider package.");
        }

        return provider;
    }
}

internal sealed class NhRepositoryProviderOptionsExtension(NhRepositoryProvider provider) : IDbContextOptionsExtension
{
    internal NhRepositoryProvider Provider { get; } = provider;

    public DbContextOptionsExtensionInfo Info => new ExtensionInfo(this);

    internal static void Configure(DbContextOptionsBuilder options, NhRepositoryProvider provider)
    {
        ((IDbContextOptionsBuilderInfrastructure)options).AddOrUpdateExtension(
            new NhRepositoryProviderOptionsExtension(provider));
    }

    public void ApplyServices(IServiceCollection services)
    {
    }

    public void Validate(IDbContextOptions options)
    {
    }

    private sealed class ExtensionInfo(NhRepositoryProviderOptionsExtension extension)
        : DbContextOptionsExtensionInfo(extension)
    {
        public override bool IsDatabaseProvider => false;

        public override string LogFragment => $"NewHeapProvider={extension.Provider.ProviderName} ";

        public override int GetServiceProviderHashCode() => extension.Provider.GetType().GetHashCode();

        public override bool ShouldUseSameServiceProvider(DbContextOptionsExtensionInfo other)
        {
            return other is ExtensionInfo info &&
                   extension.Provider.GetType() == ((NhRepositoryProviderOptionsExtension)info.Extension).Provider.GetType();
        }

        public override void PopulateDebugInfo(IDictionary<string, string> debugInfo)
        {
            debugInfo["NewHeap:RepositoryProvider"] = extension.Provider.ProviderName;
        }
    }
}
