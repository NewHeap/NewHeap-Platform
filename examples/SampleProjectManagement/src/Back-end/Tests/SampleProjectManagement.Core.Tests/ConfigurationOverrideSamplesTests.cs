using System.Text.Json;
using Microsoft.Extensions.Configuration;
using NewHeap.Platform.Common;
using Xunit;

namespace SampleProjectManagement.Core.Tests;

/// <summary>
/// SPM-096: automation may keep production appsettings while overriding the
/// host-specific secrets directory and other settings through standard command-line configuration.
/// </summary>
public sealed class ConfigurationOverrideSamplesTests
{
    [Fact]
    public void PipelineArgumentsOverrideHostSpecificSecretsPathAndFinalConfiguration()
    {
        var basePath = Path.Combine(
            Path.GetTempPath(),
            "SampleProjectManagement.Configuration",
            Guid.NewGuid().ToString("N"));
        var runnerSecretsPath = Path.Combine(basePath, "runner-secrets");

        try
        {
            Directory.CreateDirectory(runnerSecretsPath);
            WriteJson(
                Path.Combine(basePath, "appsettings.json"),
                new Dictionary<string, object?>
                {
                    ["NewHeap"] = new Dictionary<string, object?>
                    {
                        ["PlatformCommon"] = new Dictionary<string, object?>
                        {
                            ["AppSecretsDirectoryPath"] =
                                "/var/www/vhosts/shared/secrets/sample-project-management"
                        }
                    },
                    ["ConnectionStrings"] = new Dictionary<string, object?>
                    {
                        ["DefaultConnection"] =
                            "${Secrets:ConnectionStrings:DefaultConnection}"
                    },
                    ["Automation"] = new Dictionary<string, object?>
                    {
                        ["Mode"] = "from-appsettings"
                    }
                });
            WriteJson(
                Path.Combine(runnerSecretsPath, "secrets.json"),
                new Dictionary<string, object?>
                {
                    ["ConnectionStrings"] = new Dictionary<string, object?>
                    {
                        ["DefaultConnection"] = "from-runner-secrets"
                    }
                });
            var args = new[]
            {
                $"--NewHeap:PlatformCommon:AppSecretsDirectoryPath={runnerSecretsPath}",
                "--Automation:Mode=from-command-line"
            };

            var configuration = new ConfigurationBuilder()
                .ConfigureNhCommonConfiguration(basePath, args)
                .Build();

            Assert.Equal(
                runnerSecretsPath,
                configuration["NewHeap:PlatformCommon:AppSecretsDirectoryPath"]);
            Assert.Equal(
                "from-runner-secrets",
                configuration["ConnectionStrings:DefaultConnection"]);
            Assert.Equal(
                "from-command-line",
                configuration["Automation:Mode"]);
        }
        finally
        {
            if (Directory.Exists(basePath))
            {
                Directory.Delete(basePath, recursive: true);
            }
        }
    }

    private static void WriteJson(string path, Dictionary<string, object?> value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(value));
    }
}
