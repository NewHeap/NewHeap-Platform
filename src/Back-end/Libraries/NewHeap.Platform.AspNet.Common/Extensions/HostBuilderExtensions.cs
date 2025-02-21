using Microsoft.Extensions.Configuration;
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