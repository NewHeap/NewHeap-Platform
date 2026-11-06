using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace NewHeap.Platform.AspNet.Common.PostgreSql;

/// <summary>
/// How a PostgreSQL context matches the collection text matches of
/// <see cref="NewHeap.Platform.Common.Services.NhCollectionTextMatch"/>: case-insensitively with
/// <c>ILIKE</c>, which <c>UseNewHeapPostgreSql</c> configures, or case-sensitively with <c>LIKE</c>
/// after <c>UseNewHeapCaseSensitiveCollectionTextMatch</c>.
/// </summary>
internal sealed class PostgreSqlCollectionTextMatchOptionsExtension(bool caseSensitive) : IDbContextOptionsExtension
{
    internal bool CaseSensitive { get; } = caseSensitive;

    public DbContextOptionsExtensionInfo Info => new ExtensionInfo(this);

    /// <summary>
    /// Matches case-insensitively unless the context already chose, so the choice made in the
    /// <c>UseNewHeapPostgreSql</c> configure action is kept.
    /// </summary>
    internal static void ConfigureDefault(DbContextOptionsBuilder options)
    {
        if (options.Options.FindExtension<PostgreSqlCollectionTextMatchOptionsExtension>() is not null)
        {
            return;
        }

        Configure(options, caseSensitive: false);
    }

    internal static void Configure(DbContextOptionsBuilder options, bool caseSensitive)
    {
        ((IDbContextOptionsBuilderInfrastructure)options).AddOrUpdateExtension(
            new PostgreSqlCollectionTextMatchOptionsExtension(caseSensitive));
    }

    public void ApplyServices(IServiceCollection services)
    {
        // A case-sensitive context registers nothing: EF Core evaluates the marked pattern into a
        // parameter, as on every other provider, and PostgreSQL's LIKE applies.
        if (CaseSensitive)
        {
            return;
        }

        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IEvaluatableExpressionFilterPlugin,
            PostgreSqlCollectionTextMatchEvaluatableFilter>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IInterceptor,
            PostgreSqlCollectionTextMatchInterceptor>());
    }

    public void Validate(IDbContextOptions options)
    {
    }

    private sealed class ExtensionInfo(PostgreSqlCollectionTextMatchOptionsExtension extension)
        : DbContextOptionsExtensionInfo(extension)
    {
        public override bool IsDatabaseProvider => false;

        public override string LogFragment => extension.CaseSensitive
            ? "NewHeapCollectionTextMatch=CaseSensitive "
            : "NewHeapCollectionTextMatch=CaseInsensitive ";

        // The choice changes the services and with them the compiled queries, so contexts that
        // choose differently do not share a service provider or its query cache.
        public override int GetServiceProviderHashCode() => extension.CaseSensitive.GetHashCode();

        public override bool ShouldUseSameServiceProvider(DbContextOptionsExtensionInfo other)
        {
            return other is ExtensionInfo info &&
                   extension.CaseSensitive == ((PostgreSqlCollectionTextMatchOptionsExtension)info.Extension).CaseSensitive;
        }

        public override void PopulateDebugInfo(IDictionary<string, string> debugInfo)
        {
            debugInfo["NewHeap:CollectionTextMatch:CaseSensitive"] = extension.CaseSensitive.ToString();
        }
    }
}
