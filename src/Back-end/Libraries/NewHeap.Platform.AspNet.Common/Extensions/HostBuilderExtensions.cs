using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using NewHeap.Platform.Common.Extensions;

namespace NewHeap.Platform.AspNet.Common.Extensions;

public static class HostBuilderExtensions
{
    public static IHostBuilder UseNhAspnetCommonConfiguration(this IHostBuilder builder)
    {
        builder.UseNhCommonConfiguration();

        builder.ConfigureAppConfiguration(configBuilder =>
        {
            configBuilder.ConfigureNhAspNetCommonConfiguration();
        });

        return builder;
    }

    public static IConfigurationBuilder ConfigureNhAspNetCommonConfiguration(this IConfigurationBuilder configBuilder)
    {
        configBuilder.ConfigureNhCommonConfiguration();

        return configBuilder;
    }
}