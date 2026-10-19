using Microsoft.Extensions.Configuration;
using NewHeap.Platform.Common;
using SampleProjectManagement.Api.Composition;
using Xunit;

namespace SampleProjectManagement.Core.Tests;

/// <summary>
/// The sample reads its secrets, such as the review board's webhook secret (SPM-275), with
/// <see cref="NhConfigurationSecrets"/>: from <c>secrets.json</c>, which NewHeap configuration loads
/// under the <c>Secrets</c> prefix, unless a setting of the key itself wins. An unresolved
/// <c>${Secrets:…}</c> reference never counts as a secret.
/// </summary>
public sealed class ConfigurationSecretsSamplesTests
{
    private const string Key = SampleIntakeReviewerComposition.ReviewBoardSecretKey;

    [Fact]
    public void SPM_275_the_review_board_secret_comes_from_the_secrets_file()
    {
        var configuration = Configuration(new() { [$"{NhConfigurationSecrets.Prefix}:{Key}"] = "from-the-secrets-file-of-the-sample" });

        Assert.Equal("from-the-secrets-file-of-the-sample", NhConfigurationSecrets.Read(configuration, Key));
    }

    [Fact]
    public void SPM_275_a_setting_of_the_key_itself_wins_over_the_secrets_file()
    {
        var configuration = Configuration(new()
        {
            [Key] = "from-an-environment-variable",
            [$"{NhConfigurationSecrets.Prefix}:{Key}"] = "from-the-secrets-file-of-the-sample"
        });

        Assert.Equal("from-an-environment-variable", NhConfigurationSecrets.Read(configuration, Key));
    }

    [Fact]
    public void SPM_275_an_unresolved_reference_or_a_blank_value_is_no_secret()
    {
        Assert.Null(NhConfigurationSecrets.Read(Configuration(new()), Key));
        Assert.Null(NhConfigurationSecrets.Read(Configuration(new() { [Key] = " " }), Key));
        // An appsettings reference stays as literal text when secrets.json lacks the secret.
        var unresolved = Configuration(new() { [Key] = "${Secrets:" + Key + "}" });
        Assert.Null(NhConfigurationSecrets.Read(unresolved, Key));
    }

    private static IConfiguration Configuration(Dictionary<string, string?> values)
    {
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }
}
