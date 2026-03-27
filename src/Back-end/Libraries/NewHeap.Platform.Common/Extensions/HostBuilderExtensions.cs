using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using StackExchange.Utils;

namespace NewHeap.Platform.Common;

public static partial class HostBuilderExtensions
{
    private const string ConfigKey = "NewHeap:PlatformCommon:AppSecretsDirectoryPath";

    public static IHostBuilder UseNhCommonConfiguration(this IHostBuilder builder, bool environmentFileIsOptional = true)
    {
        builder.ConfigureAppConfiguration(configBuilder =>
        {
            configBuilder.ConfigureNhCommonConfiguration(environmentFileIsOptional);
        });

        builder.ConfigureLogging(logging =>
        {
            logging.ConfigureNhCommonLogging();
        });

        return builder;
    }

    public static IHostApplicationBuilder UseNhCommonConfiguration(this IHostApplicationBuilder builder, bool environmentFileIsOptional = true)
    {
        builder.Configuration.ConfigureNhCommonConfiguration(environmentFileIsOptional);
        builder.Logging.ConfigureNhCommonLogging();

        return builder;
    }

    public static IConfigurationBuilder ConfigureNhCommonConfiguration(this IConfigurationBuilder configBuilder, bool environmentFileIsOptional = true)
    {
        return configBuilder.ConfigureNhCommonConfiguration(
            basePath: Directory.GetCurrentDirectory(),
            environmentFileIsOptional: environmentFileIsOptional
        );
    }

    public static IConfigurationBuilder ConfigureNhCommonConfiguration(
        this IConfigurationBuilder configBuilder, 
        string basePath, 
        string appSettingsFileName = "appsettings",
        string secretsFileName = "secrets",
        bool environmentFileIsOptional = true
        )
    {
        basePath = Environment.ExpandEnvironmentVariables(basePath);

        appSettingsFileName = appSettingsFileName.EndsWith(".json") 
            ? appSettingsFileName.Replace(".json", "") 
            : appSettingsFileName;

        secretsFileName = secretsFileName.EndsWith(".json")
            ? secretsFileName.Replace(".json", "")
            : secretsFileName;

        //Build a config just to read the app name...
        var preConfiguration = new ConfigurationBuilder()
            .SetBasePath(basePath)
            .WithSubstitution(x => x
                .AddJsonFile(Path.Combine(basePath, $"{appSettingsFileName}.json"))
                .AddJsonFile(Path.Combine(basePath, $"{appSettingsFileName}.{Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")}.json"), environmentFileIsOptional)
            )
            .Build();

        var direcotryPath = preConfiguration.GetValue<string>(ConfigKey);

        if (string.IsNullOrWhiteSpace(direcotryPath))
        {
            throw new Exception($"Failed to read {ConfigKey} for resolving appsettings.json SECRETS file.");
        }

        configBuilder
            .WithPrefix("Secrets",
                c => c
                    .AddJsonFile(Environment.ExpandEnvironmentVariables(Path.Combine(direcotryPath, $"{secretsFileName}.json")),
                        true)
                    .AddJsonFile(
                        Environment.ExpandEnvironmentVariables(Path.Combine(direcotryPath,
                            $"{secretsFileName}.{Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")}.json")), environmentFileIsOptional)
            ).WithSubstitution(x => x
                .AddJsonFile(Path.Combine(basePath, $"{appSettingsFileName}.json"))
                .AddJsonFile(Path.Combine(basePath, $"{appSettingsFileName}.{Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")}.json"), environmentFileIsOptional)
            );

        return configBuilder;
    }

    public static ILoggingBuilder ConfigureNhCommonLogging(this ILoggingBuilder builder)
    {
        builder.AddOpenTelemetry(logging =>
        {
            logging.IncludeFormattedMessage = true;
            logging.IncludeScopes = true;
        });

        return builder;
    }
}