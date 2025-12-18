using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NewHeap.Platform.Common;

namespace NewHeap.Platform.AspNet.Common;

public static partial class HostBuilderExtensions
{
    public static IHostBuilder UseNewHeapAspnetCommonConfiguration(this IHostBuilder builder)
    {
        builder.UseNhCommonConfiguration();

        builder.ConfigureAppConfiguration(configBuilder =>
        {
            configBuilder.ConfigureNewHeapAspNetCommonConfiguration();
        });

        return builder;
    }

    public static IHostApplicationBuilder UseNewHeapAspnetCommonConfiguration(this IHostApplicationBuilder builder)
    {
        builder.UseNhCommonConfiguration();
        builder.Configuration.ConfigureNewHeapAspNetCommonConfiguration();

        return builder;
    }

    public static IConfigurationBuilder ConfigureNewHeapAspNetCommonConfiguration(this IConfigurationBuilder configBuilder)
    {
        configBuilder.ConfigureNewHeapAspNetCommonConfiguration(
            basePath: Directory.GetCurrentDirectory()
        );

        return configBuilder;
    }

    public static IWebHostBuilder UseNewHeapDevelopmentKestrelDefaults(
        this IWebHostBuilder builder, 
        Action<KestrelServerOptions>? serverOptions = null, 
        string? forcedEnvironmentName = "Development"
        )
    {
        return builder.ConfigureServices((context, app) =>
        {
            var env = context.HostingEnvironment;
            app.UseNewHeapDevelopmentKestrelDefaults(
                env.EnvironmentName,
                serverOptions,
                forcedEnvironmentName
            );
        });
    }

    public static IHostApplicationBuilder UseNewHeapDevelopmentKestrelDefaults(
        this IHostApplicationBuilder builder, 
        Action<KestrelServerOptions>? serverOptions = null, 
        string? forcedEnvironmentName = "Development")
    {
        var env = builder.Environment;
        builder.Services.UseNewHeapDevelopmentKestrelDefaults(env.EnvironmentName, serverOptions, forcedEnvironmentName);

        return builder;
    }

    private static IServiceCollection UseNewHeapDevelopmentKestrelDefaults(
        this IServiceCollection services, 
        string environmentName,
        Action<KestrelServerOptions>? serverOptions = null, 
        string? forcedEnvironmentName = "Development")
    {
        services.Configure<KestrelServerOptions>(options =>
        {
            if (!string.IsNullOrWhiteSpace(forcedEnvironmentName))
            {
                if (!string.Equals(environmentName, forcedEnvironmentName, StringComparison.InvariantCultureIgnoreCase))
                {
                    return;
                }
            }

            options.Limits.MaxRequestBodySize = 99999999999999999;
            options.Limits.MaxConcurrentConnections = 20000;
            options.Limits.MaxConcurrentUpgradedConnections = 20000;

            options.Limits.KeepAliveTimeout = TimeSpan.FromMinutes(5);
            options.Limits.RequestHeadersTimeout = TimeSpan.FromMinutes(5);

            serverOptions?.Invoke(options);
        });

        return services;
    }

    public static IConfigurationBuilder ConfigureNewHeapAspNetCommonConfiguration(
        this IConfigurationBuilder configBuilder,
        string basePath,
        string appSettingsFileName = "appsettings",
        string secretsFileName = "secrets"
        )
    {
        configBuilder.ConfigureNhCommonConfiguration(
            basePath: basePath,
            appSettingsFileName: appSettingsFileName,
            secretsFileName: secretsFileName
        );

        return configBuilder;
    }
}