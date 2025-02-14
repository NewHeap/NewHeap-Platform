using System;
using System.IO;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using StackExchange.Utils;

namespace NewHeap.Platform.Common.Extensions;
public static class HostBuilderExtensions
{
    public static IHostBuilder UseAppConfiguration(this IHostBuilder builder)
    {
        builder.ConfigureAppConfiguration(configBuilder =>
        {
            configBuilder.ConfigureAppConfiguration();
        });

        return builder;
    }

    public static IConfigurationBuilder ConfigureAppConfiguration(this IConfigurationBuilder configBuilder)
    {
        //Build a config just to read the app name...
        IConfigurationRoot preConfiguration = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .WithSubstitution(x => x
                .AddJsonFile("appsettings.json")
                .AddJsonFile($"appsettings.{Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")}.json", true)
            )
            .Build();

        var direcotryPath = preConfiguration.GetValue<string>("NewHeap:Platform:Common:AppSecretsDirectoryPath");

        if (string.IsNullOrWhiteSpace(direcotryPath))
        {
            throw new Exception("Failed to read AppSettings:AppName for resolving appsettings.json SECRETS file.");
        }

        configBuilder
            .WithPrefix("Secrets",
                c => c
                    .AddJsonFile(Environment.ExpandEnvironmentVariables(Path.Combine(direcotryPath, "secrets.json")), true)
                    .AddJsonFile(Environment.ExpandEnvironmentVariables(Path.Combine(direcotryPath, $"secrets.{Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")}.json")), true)
            ).WithSubstitution(x => x
                .AddJsonFile("appsettings.json")
                .AddJsonFile($"appsettings.{Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")}.json", true)
            );

        return configBuilder;
    }
}