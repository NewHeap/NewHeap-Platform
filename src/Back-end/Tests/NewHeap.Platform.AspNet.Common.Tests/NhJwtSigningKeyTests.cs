using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;
using NewHeap.Platform.AspNet.Common.Extensions;
using NewHeap.Platform.AspNet.Common.Models.Options;
using Xunit;

namespace NewHeap.Platform.AspNet.Common.Tests;

/// <summary>
/// The JWT signing key fails closed: a missing secret, its <c>${Secrets:…}</c> reference left as text,
/// or a key shorter than HMAC-SHA256 needs stops the platform instead of signing with a known value.
/// </summary>
public sealed class NhJwtSigningKeyTests
{
    private const string Key = NhJwtSigningKey.ConfigurationKey;
    private const string UsableKey = "a-random-test-signing-key-of-forty-bytes";

    [Fact]
    public void A_configured_key_from_the_settings_or_the_secrets_file_is_used()
    {
        Assert.Equal(UsableKey, NhJwtSigningKey.Read(Configuration(new() { [Key] = UsableKey })));
        Assert.Equal(UsableKey, NhJwtSigningKey.Read(Configuration(new() { ["Secrets:" + Key] = UsableKey })));
        Assert.Equal(UsableKey, NhJwtSigningKey.Read(Configuration(new()
        {
            [Key] = "${Secrets:" + Key + "}",
            ["Secrets:" + Key] = UsableKey
        })));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("${Secrets:NewHeap:PlatformAspNetCommon:Authorization:JWT:Token:Key}")]
    public void A_missing_key_or_its_unresolved_reference_is_refused(string? value)
    {
        var failure = Assert.Throws<InvalidOperationException>(() => NhJwtSigningKey.Read(Configuration(new() { [Key] = value })));

        Assert.StartsWith(Key + " is not configured", failure.Message, StringComparison.Ordinal);
        if (!string.IsNullOrEmpty(value))
        {
            Assert.DoesNotContain(value, failure.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void A_key_shorter_than_256_bits_is_refused_without_showing_it()
    {
        const string shortKey = "too-short-signing-key";

        var failure = Assert.Throws<InvalidOperationException>(() => NhJwtSigningKey.Read(Configuration(new() { [Key] = shortKey })));

        Assert.Contains($"at least {NhJwtSigningKey.MinimumBytes} bytes", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(shortKey, failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_platform_options_refuse_to_validate_tokens_with_an_unresolved_key()
    {
        var unresolved = NewHeapAspNetCommonOptions.Builder(Configuration(new() { [Key] = "${Secrets:" + Key + "}" })).Build();
        var configured = NewHeapAspNetCommonOptions.Builder(Configuration(new() { [Key] = UsableKey })).Build();

        Assert.Throws<InvalidOperationException>(() =>
            unresolved.JwtBearerOptionsTokenValidationParametersAction(new TokenValidationParameters()));
        var parameters = new TokenValidationParameters();
        configured.JwtBearerOptionsTokenValidationParametersAction(parameters);
        Assert.IsType<SymmetricSecurityKey>(parameters.IssuerSigningKey);
    }

    private static IConfiguration Configuration(Dictionary<string, string?> values)
    {
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }
}
