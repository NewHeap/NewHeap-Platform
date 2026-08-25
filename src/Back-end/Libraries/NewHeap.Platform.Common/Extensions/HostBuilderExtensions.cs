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
        return ConfigureNhCommonHost(builder, null, environmentFileIsOptional);
    }

    public static IHostBuilder UseNhCommonConfiguration(
        this IHostBuilder builder,
        string[] args,
        bool environmentFileIsOptional = true)
    {
        ArgumentNullException.ThrowIfNull(args);

        return ConfigureNhCommonHost(builder, args, environmentFileIsOptional);
    }

    private static IHostBuilder ConfigureNhCommonHost(
        IHostBuilder builder,
        string[]? args,
        bool environmentFileIsOptional)
    {
        builder.ConfigureAppConfiguration((context, configBuilder) =>
        {
            ConfigureNhCommonConfigurationCore(
                configBuilder,
                basePath: context.HostingEnvironment.ContentRootPath,
                environmentName: context.HostingEnvironment.EnvironmentName,
                args: args,
                appSettingsFileName: "appsettings",
                secretsFileName: "secrets",
                environmentFileIsOptional: environmentFileIsOptional
            );
        });

        builder.AddNewHeapObservability();

        return builder;
    }

    public static IHostApplicationBuilder UseNhCommonConfiguration(this IHostApplicationBuilder builder, bool environmentFileIsOptional = true)
    {
        return ConfigureNhCommonHost(builder, null, environmentFileIsOptional);
    }

    public static IHostApplicationBuilder UseNhCommonConfiguration(
        this IHostApplicationBuilder builder,
        string[] args,
        bool environmentFileIsOptional = true)
    {
        ArgumentNullException.ThrowIfNull(args);

        return ConfigureNhCommonHost(builder, args, environmentFileIsOptional);
    }

    private static IHostApplicationBuilder ConfigureNhCommonHost(
        IHostApplicationBuilder builder,
        string[]? args,
        bool environmentFileIsOptional)
    {
        ConfigureNhCommonConfigurationCore(
            builder.Configuration,
            basePath: builder.Environment.ContentRootPath,
            environmentName: builder.Environment.EnvironmentName,
            args: args,
            appSettingsFileName: "appsettings",
            secretsFileName: "secrets",
            environmentFileIsOptional: environmentFileIsOptional
        );
        builder.AddNewHeapObservability();

        return builder;
    }

    public static IConfigurationBuilder ConfigureNhCommonConfiguration(this IConfigurationBuilder configBuilder, bool environmentFileIsOptional = true)
    {
        return ConfigureNhCommonConfigurationCore(
            configBuilder,
            basePath: Directory.GetCurrentDirectory(),
            environmentName: GetEnvironmentName(),
            args: null,
            appSettingsFileName: "appsettings",
            secretsFileName: "secrets",
            environmentFileIsOptional: environmentFileIsOptional
        );
    }

    public static IConfigurationBuilder ConfigureNhCommonConfiguration(
        this IConfigurationBuilder configBuilder,
        string[] args,
        bool environmentFileIsOptional = true)
    {
        ArgumentNullException.ThrowIfNull(args);

        return ConfigureNhCommonConfigurationCore(
            configBuilder,
            basePath: Directory.GetCurrentDirectory(),
            environmentName: GetEnvironmentName(),
            args: args,
            appSettingsFileName: "appsettings",
            secretsFileName: "secrets",
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
        return ConfigureNhCommonConfigurationCore(
            configBuilder,
            basePath,
            GetEnvironmentName(),
            null,
            appSettingsFileName,
            secretsFileName,
            environmentFileIsOptional
        );
    }

    public static IConfigurationBuilder ConfigureNhCommonConfiguration(
        this IConfigurationBuilder configBuilder,
        string basePath,
        string[] args,
        string appSettingsFileName = "appsettings",
        string secretsFileName = "secrets",
        bool environmentFileIsOptional = true
        )
    {
        ArgumentNullException.ThrowIfNull(args);

        return ConfigureNhCommonConfigurationCore(
            configBuilder,
            basePath,
            GetEnvironmentName(),
            args,
            appSettingsFileName,
            secretsFileName,
            environmentFileIsOptional
        );
    }

    private static IConfigurationBuilder ConfigureNhCommonConfigurationCore(
        IConfigurationBuilder configBuilder,
        string basePath,
        string environmentName,
        string[]? args,
        string appSettingsFileName,
        string secretsFileName,
        bool environmentFileIsOptional
        )
    {
        basePath = Environment.ExpandEnvironmentVariables(basePath);

        appSettingsFileName = appSettingsFileName.EndsWith(".json") 
            ? appSettingsFileName.Replace(".json", "") 
            : appSettingsFileName;

        secretsFileName = secretsFileName.EndsWith(".json")
            ? secretsFileName.Replace(".json", "")
            : secretsFileName;

        var overridesBuilder = new ConfigurationBuilder()
            .AddEnvironmentVariables();

        if (args is { Length: > 0 })
        {
            overridesBuilder.AddCommandLine(args);
        }

        var overrides = overridesBuilder.Build();

        // Build a config just to resolve the secrets directory.
        var preConfiguration = new ConfigurationBuilder()
            .SetBasePath(basePath)
            .WithSubstitution(x => x
                .AddJsonFile(Path.Combine(basePath, $"{appSettingsFileName}.json"))
                .AddJsonFile(Path.Combine(basePath, $"{appSettingsFileName}.{environmentName}.json"), environmentFileIsOptional)
            )
            .AddConfiguration(overrides)
            .Build();

        var directoryPath = preConfiguration.GetValue<string>(ConfigKey);

        if (string.IsNullOrWhiteSpace(directoryPath))
        {
            throw new Exception($"Failed to read {ConfigKey} for resolving appsettings.json SECRETS file.");
        }

        configBuilder
            .WithPrefix("Secrets",
                c => c
                    .AddJsonFile(Environment.ExpandEnvironmentVariables(Path.Combine(directoryPath, $"{secretsFileName}.json")),
                        true)
                    .AddJsonFile(
                        Environment.ExpandEnvironmentVariables(Path.Combine(directoryPath,
                            $"{secretsFileName}.{environmentName}.json")), environmentFileIsOptional)
            ).WithSubstitution(x => x
                .AddJsonFile(Path.Combine(basePath, $"{appSettingsFileName}.json"))
                .AddJsonFile(Path.Combine(basePath, $"{appSettingsFileName}.{environmentName}.json"), environmentFileIsOptional)
            )
            .AddConfiguration(overrides);

        return configBuilder;
    }

    private static string GetEnvironmentName()
    {
        return Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT")
            ?? Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")
            ?? Environments.Production;
    }

    public static ILoggingBuilder ConfigureNhCommonLogging(
        this ILoggingBuilder builder,
        Models.Options.NewHeapObservabilityOptions? options = null)
    {
        options ??= new Models.Options.NewHeapObservabilityOptions();
        builder.AddOpenTelemetry(logging =>
        {
            logging.IncludeFormattedMessage = options.IncludeFormattedMessage;
            logging.IncludeScopes = options.IncludeScopes;
            logging.ParseStateValues = options.ParseStateValues;
            options.ConfigureLogging?.Invoke(logging);
        });

        return builder;
    }
}
