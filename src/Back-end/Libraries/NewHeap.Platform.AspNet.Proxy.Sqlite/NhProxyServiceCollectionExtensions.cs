using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace NewHeap.Platform.AspNet.Proxy.Sqlite;

/// <summary>Registers startup options and YARP. Managed rules, administration, and SQLite storage are not implemented yet.</summary>
public static class NhProxyServiceCollectionExtensions
{
    /// <summary>Binds proxy options from the supplied section and storage options from its Sqlite child.</summary>
    public static IServiceCollection AddNewHeapProxy(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        return services.AddNewHeapProxy(
            options => configuration.Bind(options),
            options => configuration.GetSection(NhProxySqliteOptions.ConfigurationSectionName).Bind(options));
    }

    public static IServiceCollection AddNewHeapProxy(
        this IServiceCollection services,
        Action<NhProxyOptions>? configure = null,
        Action<NhProxySqliteOptions>? configureStorage = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = new NhProxyOptions();
        var storageOptions = new NhProxySqliteOptions();
        configure?.Invoke(options);
        configureStorage?.Invoke(storageOptions);

        services.AddSingleton<IOptions<NhProxyOptions>>(Options.Create(options));
        services.AddSingleton<IOptions<NhProxySqliteOptions>>(Options.Create(storageOptions));

        var yarp = services.AddReverseProxy().LoadFromMemory([], []);
        options.YarpConfiguration?.Invoke(yarp);

        return services;
    }
}
