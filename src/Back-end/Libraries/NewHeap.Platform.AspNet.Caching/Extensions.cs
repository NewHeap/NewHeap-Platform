using NeoSmart.Caching.Sqlite;
using ZiggyCreatures.Caching.Fusion;
using ZiggyCreatures.Caching.Fusion.Serialization.NewtonsoftJson;

// ReSharper disable once CheckNamespace
namespace NewHeap.Platform.AspNet;

public static class Extensions
{
    /// <summary>
    /// Add default NewHeap Platform caching to the application. Configures a memory cache.
    /// </summary>
    /// <param name="builder"></param>
    /// <param name="configure"></param>
    /// <returns></returns>
    public static WebApplicationBuilder AddNewHeapPlatformCachingDefault(this WebApplicationBuilder builder, Action<FusionCacheOptions>? configure = null)
    {
        var options = new FusionCacheOptions()
        {
            DefaultEntryOptions = new FusionCacheEntryOptions()
            {
                Duration = TimeSpan.FromMinutes(5),
                JitterMaxDuration = TimeSpan.FromMinutes(1),
                EagerRefreshThreshold = 0.1F, // Allow eager refresh when <= 10% of the cache period is remaining
            }
        };
        configure?.Invoke(options);
        builder.Services.AddFusionCache()
            .WithOptions(options)
            .WithSerializer(new FusionCacheNewtonsoftJsonSerializer())
            ;
        return builder;
    }
    
    /// <summary>
    /// Add FusionCache to the application. Does not do any extra configuration other than the defaults done by FusionCache.
    /// </summary>
    /// <param name="builder"></param>
    /// <param name="configure"></param>
    /// <returns></returns>
    public static WebApplicationBuilder AddNewHeapPlatformCaching(this WebApplicationBuilder builder, Action<IFusionCacheBuilder>? configure = null)
    {
        var fcBuilder = builder.Services.AddFusionCache();
        configure?.Invoke(fcBuilder);
        return builder;
    }
    
    
    /// <summary>
    /// Add NewHeap Platform caching to the application. Configures memory cache as L1 cache and SQLite as L2 cache.
    /// Sqlite is persisted to disk so it can be reused between application restarts.
    /// </summary>
    /// <param name="builder"></param>
    /// <param name="configureCache"></param>
    /// <param name="configureSqlite"></param>
    /// <returns></returns>
    public static WebApplicationBuilder AddNewHeapPlatformDiskCaching(this WebApplicationBuilder builder,
        Action<FusionCacheOptions>? configureCache = null,
        Action<SqliteCacheOptions>? configureSqlite = null
        )
    {
        var sqliteOptions = new SqliteCacheOptions()
        {
            CachePath = "cache.sqlite"
        };
        configureSqlite?.Invoke(sqliteOptions);

        var options = new FusionCacheOptions();
        configureCache?.Invoke(options);
        
        AddNewHeapPlatformCaching(builder, b =>
        {
            b.WithDistributedCache(new SqliteCache(sqliteOptions))
                .WithSerializer(new FusionCacheNewtonsoftJsonSerializer())
                .WithOptions(options);
        });
        
        return builder;
    }
}