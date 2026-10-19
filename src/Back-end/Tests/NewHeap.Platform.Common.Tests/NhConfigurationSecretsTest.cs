using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NewHeap.Platform.Common.Models.Options;
using Xunit;

namespace NewHeap.Platform.Common.Tests;

[Collection(NhConfigurationEnvironmentCollection.CollectionName)]
public sealed class NhConfigurationSecretsTest
{
    private const string SecretsDirectoryEnvironmentKey = "NewHeap__PlatformCommon__AppSecretsDirectoryPath";

    [Fact]
    public void Read_prefers_the_key_itself_over_the_secrets_files()
    {
        var configuration = InMemory(new()
        {
            ["App:Secret"] = "from-an-environment-variable",
            ["Secrets:App:Secret"] = "from-the-secrets-file"
        });

        Assert.Equal("from-an-environment-variable", NhConfigurationSecrets.Read(configuration, "App:Secret"));
        Assert.Equal(
            "from-the-secrets-file",
            NhConfigurationSecrets.Read(InMemory(new() { ["Secrets:App:Secret"] = "from-the-secrets-file" }), "App:Secret"));
    }

    [Fact]
    public void Read_treats_blank_values_and_unresolved_references_as_missing()
    {
        Assert.Null(NhConfigurationSecrets.Read(InMemory(new()), "App:Secret"));
        Assert.Null(NhConfigurationSecrets.Read(InMemory(new() { ["App:Secret"] = "  " }), "App:Secret"));
        Assert.Null(NhConfigurationSecrets.Read(InMemory(new() { ["App:Secret"] = "${Secrets:App:Secret}" }), "App:Secret"));
        Assert.True(NhConfigurationSecrets.IsUnresolvedReference("prefix-${Secrets:App:Secret}"));
        Assert.False(NhConfigurationSecrets.IsUnresolvedReference("a$b{c}"));
    }

    [Fact]
    public void A_missing_secret_keeps_its_reference_as_text_which_reading_and_reporting_catch()
    {
        var basePath = Path.Combine(Path.GetTempPath(), "NewHeap.Platform.Common.Test", Guid.NewGuid().ToString("N"));
        var secretsDirectory = Path.Combine(basePath, "secrets");
        Directory.CreateDirectory(secretsDirectory);
        try
        {
            File.WriteAllText(Path.Combine(basePath, "appsettings.json"), JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["NewHeap"] = new Dictionary<string, object?> { ["PlatformCommon"] = new Dictionary<string, object?> { ["AppSecretsDirectoryPath"] = secretsDirectory } },
                ["App"] = new Dictionary<string, object?>
                {
                    ["Present"] = "${Secrets:App:Present}",
                    ["Missing"] = "${Secrets:App:Missing}"
                }
            }));
            File.WriteAllText(
                Path.Combine(secretsDirectory, "secrets.json"),
                JsonSerializer.Serialize(new { App = new { Present = "a-configured-secret" } }));
            var original = Environment.GetEnvironmentVariable(SecretsDirectoryEnvironmentKey);
            Environment.SetEnvironmentVariable(SecretsDirectoryEnvironmentKey, null);
            IConfiguration configuration;
            try
            {
                configuration = new ConfigurationBuilder().ConfigureNhCommonConfiguration(basePath: basePath).Build();
            }
            finally
            {
                Environment.SetEnvironmentVariable(SecretsDirectoryEnvironmentKey, original);
            }

            // The substitution leaves the reference of a missing secret as text.
            Assert.Equal("${Secrets:App:Missing}", configuration["App:Missing"]);
            Assert.Null(NhConfigurationSecrets.Read(configuration, "App:Missing"));
            Assert.Equal("a-configured-secret", NhConfigurationSecrets.Read(configuration, "App:Present"));
            var unresolved = NhConfigurationSecrets.FindUnresolvedReferences(configuration);
            Assert.Equal(["App:Missing"], unresolved);
        }
        finally
        {
            Directory.Delete(basePath, recursive: true);
        }
    }

    [Fact]
    public void The_common_platform_reports_unresolved_references_at_startup()
    {
        var services = new ServiceCollection();
        services.AddNewHeapPlatformCommon(NewHeapCommonOptions.Builder(InMemory(new())).Build());

        Assert.Contains(
            services,
            descriptor => descriptor.ServiceType == typeof(IHostedService)
                && descriptor.ImplementationType?.Name == "NhUnresolvedConfigurationReferenceReporter");
    }

    private static IConfiguration InMemory(Dictionary<string, string?> values)
    {
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }
}
