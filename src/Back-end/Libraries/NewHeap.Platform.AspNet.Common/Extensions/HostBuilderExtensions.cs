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

    public static IConfigurationBuilder ConfigureNewHeapAspNetCommonConfiguration(this IConfigurationBuilder configBuilder)
    {
        configBuilder.ConfigureNhCommonConfiguration();

        return configBuilder;
    }
}