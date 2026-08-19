using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace NewHeap.Platform.Common.Tests;

[CollectionDefinition(CollectionName, DisableParallelization = true)]
public sealed class NhConfigurationEnvironmentCollection
{
    public const string CollectionName = "Nh configuration environment";
}

[Collection(NhConfigurationEnvironmentCollection.CollectionName)]
public sealed class HostBuilderExtensionsTest
{
    private const string SecretsDirectoryEnvironmentKey =
        "NewHeap__PlatformCommon__AppSecretsDirectoryPath";

    private const string FinalValueEnvironmentKey =
        "NhConfigurationOverrideTest__Value";

    [Fact]
    public void ConfigureNhCommonConfiguration_UsesEnvironmentOverridesForBootstrapAndFinalConfiguration()
    {
        using var fixture = ConfigurationFixture.Create();
        using var secretsDirectory = new EnvironmentVariableScope(
            SecretsDirectoryEnvironmentKey,
            fixture.EnvironmentSecretsDirectory);
        using var finalValue = new EnvironmentVariableScope(
            FinalValueEnvironmentKey,
            "from-environment");

        var configuration = new ConfigurationBuilder()
            .ConfigureNhCommonConfiguration(basePath: fixture.BasePath)
            .Build();

        Assert.Equal(
            "from-environment-secrets",
            configuration["ConnectionStrings:DefaultConnection"]);
        Assert.Equal(
            "from-environment",
            configuration["NhConfigurationOverrideTest:Value"]);
        Assert.Equal(
            fixture.EnvironmentSecretsDirectory,
            configuration["NewHeap:PlatformCommon:AppSecretsDirectoryPath"]);
    }

    [Fact]
    public void ConfigureNhCommonConfiguration_CommandLineOverridesEnvironmentVariables()
    {
        using var fixture = ConfigurationFixture.Create();
        using var secretsDirectory = new EnvironmentVariableScope(
            SecretsDirectoryEnvironmentKey,
            fixture.EnvironmentSecretsDirectory);
        using var finalValue = new EnvironmentVariableScope(
            FinalValueEnvironmentKey,
            "from-environment");
        var args = new[]
        {
            $"--NewHeap:PlatformCommon:AppSecretsDirectoryPath={fixture.CommandLineSecretsDirectory}",
            "--NhConfigurationOverrideTest:Value=from-command-line"
        };

        var configuration = new ConfigurationBuilder()
            .ConfigureNhCommonConfiguration(
                basePath: fixture.BasePath,
                args: args)
            .Build();

        Assert.Equal(
            "from-command-line-secrets",
            configuration["ConnectionStrings:DefaultConnection"]);
        Assert.Equal(
            "from-command-line",
            configuration["NhConfigurationOverrideTest:Value"]);
        Assert.Equal(
            fixture.CommandLineSecretsDirectory,
            configuration["NewHeap:PlatformCommon:AppSecretsDirectoryPath"]);
    }

    [Fact]
    public void ConfigureNhCommonConfiguration_WithoutOverridesRetainsFileConfiguration()
    {
        using var fixture = ConfigurationFixture.Create();
        using var secretsDirectory = new EnvironmentVariableScope(
            SecretsDirectoryEnvironmentKey,
            null);
        using var finalValue = new EnvironmentVariableScope(
            FinalValueEnvironmentKey,
            null);

        var configuration = new ConfigurationBuilder()
            .ConfigureNhCommonConfiguration(basePath: fixture.BasePath)
            .Build();

        Assert.Equal(
            "from-file-secrets",
            configuration["ConnectionStrings:DefaultConnection"]);
        Assert.Equal(
            "from-json",
            configuration["NhConfigurationOverrideTest:Value"]);
    }

    [Fact]
    public void UseNhCommonConfiguration_UsesTheHostEnvironmentAndContentRoot()
    {
        using var fixture = ConfigurationFixture.Create();
        using var secretsDirectory = new EnvironmentVariableScope(
            SecretsDirectoryEnvironmentKey,
            null);
        fixture.WriteEnvironmentAppSettings(
            "Pipeline",
            fixture.EnvironmentSecretsDirectory);
        var builder = Host.CreateEmptyApplicationBuilder(
            new HostApplicationBuilderSettings
            {
                ContentRootPath = fixture.BasePath,
                EnvironmentName = "Pipeline"
            });

        builder.UseNhCommonConfiguration(Array.Empty<string>());

        Assert.Equal(
            fixture.EnvironmentSecretsDirectory,
            builder.Configuration["NewHeap:PlatformCommon:AppSecretsDirectoryPath"]);
        Assert.Equal(
            "from-environment-secrets",
            builder.Configuration["ConnectionStrings:DefaultConnection"]);
    }

    private sealed class ConfigurationFixture : IDisposable
    {
        private ConfigurationFixture(string basePath)
        {
            BasePath = basePath;
            FileSecretsDirectory = Path.Combine(basePath, "file-secrets");
            EnvironmentSecretsDirectory = Path.Combine(basePath, "environment-secrets");
            CommandLineSecretsDirectory = Path.Combine(basePath, "command-line-secrets");
        }

        public string BasePath { get; }

        public string FileSecretsDirectory { get; }

        public string EnvironmentSecretsDirectory { get; }

        public string CommandLineSecretsDirectory { get; }

        public static ConfigurationFixture Create()
        {
            var basePath = Path.Combine(
                Path.GetTempPath(),
                "NewHeap.Platform.Common.Test",
                Guid.NewGuid().ToString("N"));
            var fixture = new ConfigurationFixture(basePath);

            Directory.CreateDirectory(fixture.BasePath);
            WriteSecrets(fixture.FileSecretsDirectory, "from-file-secrets");
            WriteSecrets(fixture.EnvironmentSecretsDirectory, "from-environment-secrets");
            WriteSecrets(fixture.CommandLineSecretsDirectory, "from-command-line-secrets");

            var appSettings = new Dictionary<string, object?>
            {
                ["NewHeap"] = new Dictionary<string, object?>
                {
                    ["PlatformCommon"] = new Dictionary<string, object?>
                    {
                        ["AppSecretsDirectoryPath"] = fixture.FileSecretsDirectory
                    }
                },
                ["ConnectionStrings"] = new Dictionary<string, object?>
                {
                    ["DefaultConnection"] = "${Secrets:ConnectionStrings:DefaultConnection}"
                },
                ["NhConfigurationOverrideTest"] = new Dictionary<string, object?>
                {
                    ["Value"] = "from-json"
                }
            };
            File.WriteAllText(
                Path.Combine(fixture.BasePath, "appsettings.json"),
                JsonSerializer.Serialize(appSettings));

            return fixture;
        }

        public void WriteEnvironmentAppSettings(
            string environmentName,
            string secretsDirectory)
        {
            var appSettings = new Dictionary<string, object?>
            {
                ["NewHeap"] = new Dictionary<string, object?>
                {
                    ["PlatformCommon"] = new Dictionary<string, object?>
                    {
                        ["AppSecretsDirectoryPath"] = secretsDirectory
                    }
                }
            };
            File.WriteAllText(
                Path.Combine(BasePath, $"appsettings.{environmentName}.json"),
                JsonSerializer.Serialize(appSettings));
        }

        public void Dispose()
        {
            Directory.Delete(BasePath, recursive: true);
        }

        private static void WriteSecrets(string directory, string connectionString)
        {
            Directory.CreateDirectory(directory);
            var secrets = new Dictionary<string, object?>
            {
                ["ConnectionStrings"] = new Dictionary<string, object?>
                {
                    ["DefaultConnection"] = connectionString
                }
            };
            File.WriteAllText(
                Path.Combine(directory, "secrets.json"),
                JsonSerializer.Serialize(secrets));
        }
    }

    private sealed class EnvironmentVariableScope : IDisposable
    {
        private readonly string _key;
        private readonly string? _originalValue;

        public EnvironmentVariableScope(string key, string? value)
        {
            _key = key;
            _originalValue = Environment.GetEnvironmentVariable(key);
            Environment.SetEnvironmentVariable(key, value);
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable(_key, _originalValue);
        }
    }
}
