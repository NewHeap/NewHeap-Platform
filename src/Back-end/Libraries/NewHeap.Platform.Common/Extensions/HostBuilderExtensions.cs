using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using StackExchange.Utils;

namespace NewHeap.Platform.Common;

public static partial class HostBuilderExtensions
{
    private const string ConfigKey = "NewHeap:PlatformCommon:AppSecretsDirectoryPath";

    public static IHostBuilder UseNhCommonConfiguration(this IHostBuilder builder)
    {
        builder.ConfigureAppConfiguration(configBuilder =>
        {
            configBuilder.ConfigureNhCommonConfiguration();
        });

        return builder;
    }

    public static IConfigurationBuilder ConfigureNhCommonConfiguration(this IConfigurationBuilder configBuilder)
    {
        //Build a config just to read the app name...
        var preConfiguration = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .WithSubstitution(x => x
                .AddJsonFile("appsettings.json")
                .AddJsonFile($"appsettings.{Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")}.json", true)
            )
            .Build();

        var direcotryPath = preConfiguration.GetValue<string>(ConfigKey);

        if (string.IsNullOrWhiteSpace(direcotryPath))
        {
            throw new Exception($"Failed to read {ConfigKey} for resolving appsettings.json SECRETS file.");
        }

        direcotryPath = Environment.ExpandEnvironmentVariables(direcotryPath);

        configBuilder
            .WithPrefix("Secrets",
                c => c
                    .AddJsonFile(Environment.ExpandEnvironmentVariables(Path.Combine(direcotryPath, "secrets.json")),
                        true)
                    .AddJsonFile(
                        Environment.ExpandEnvironmentVariables(Path.Combine(direcotryPath,
                            $"secrets.{Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")}.json")), true)
            ).WithSubstitution(x => x
                .AddJsonFile("appsettings.json")
                .AddJsonFile($"appsettings.{Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")}.json", true)
            );

        return configBuilder;
    }
}