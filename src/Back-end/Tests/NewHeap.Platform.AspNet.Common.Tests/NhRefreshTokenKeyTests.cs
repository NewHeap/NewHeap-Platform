using Microsoft.Extensions.Configuration;
using NewHeap.Platform.AspNet.Common.Extensions;
using Xunit;

namespace NewHeap.Platform.AspNet.Common.Tests;

public sealed class NhRefreshTokenKeyTests
{
    private const string SigningKey = "refresh-key-tests-signing-key-of-thirty-two-bytes";
    private const string RefreshKey = "refresh-key-tests-refresh-key-of-thirty-two-bytes";
    private const string Token = "presented-refresh-token";

    [Fact]
    public void TheDigestIsKeyedVersionedAndRepeatable()
    {
        var configuration = Configuration((NhJwtSigningKey.ConfigurationKey, SigningKey));

        var digest = NhRefreshTokenKey.Digest(configuration, Token);

        Assert.Equal(digest, NhRefreshTokenKey.Digest(configuration, Token));
        Assert.Matches("^nhr1:[0-9a-f]{64}$", digest);
        Assert.NotEqual(digest, NhRefreshTokenKey.Digest(configuration, Token + "x"));
        Assert.NotEqual(
            digest,
            NhRefreshTokenKey.Digest(Configuration((NhJwtSigningKey.ConfigurationKey, SigningKey + "x")), Token));
    }

    [Fact]
    public void TheRefreshTokenKeyTakesPrecedenceOverTheSigningKey()
    {
        var signingKeyOnly = Configuration((NhJwtSigningKey.ConfigurationKey, SigningKey));
        var both = Configuration(
            (NhJwtSigningKey.ConfigurationKey, SigningKey),
            (NhRefreshTokenKey.ConfigurationKey, RefreshKey));
        var refreshKeyOnly = Configuration((NhRefreshTokenKey.ConfigurationKey, RefreshKey));

        Assert.NotEqual(NhRefreshTokenKey.Digest(signingKeyOnly, Token), NhRefreshTokenKey.Digest(both, Token));
        Assert.Equal(NhRefreshTokenKey.Digest(both, Token), NhRefreshTokenKey.Digest(refreshKeyOnly, Token));
    }

    [Fact]
    public void TheKeysAreReadFromTheSecretsFiles()
    {
        var fromSecrets = Configuration(
            (NhRefreshTokenKey.ConfigurationKey, "${Secrets:" + NhRefreshTokenKey.ConfigurationKey + "}"),
            ("Secrets:" + NhRefreshTokenKey.ConfigurationKey, RefreshKey));

        Assert.Equal(
            NhRefreshTokenKey.Digest(Configuration((NhRefreshTokenKey.ConfigurationKey, RefreshKey)), Token),
            NhRefreshTokenKey.Digest(fromSecrets, Token));
    }

    [Fact]
    public void AnUnresolvedRefreshTokenKeyFallsBackToTheSigningKey()
    {
        var unresolved = Configuration(
            (NhJwtSigningKey.ConfigurationKey, SigningKey),
            (NhRefreshTokenKey.ConfigurationKey, "${Secrets:" + NhRefreshTokenKey.ConfigurationKey + "}"));

        Assert.Equal(
            NhRefreshTokenKey.Digest(Configuration((NhJwtSigningKey.ConfigurationKey, SigningKey)), Token),
            NhRefreshTokenKey.Digest(unresolved, Token));
    }

    [Fact]
    public void AShortRefreshTokenKeyIsRefusedWithoutItsValue()
    {
        var configuration = Configuration(
            (NhJwtSigningKey.ConfigurationKey, SigningKey),
            (NhRefreshTokenKey.ConfigurationKey, "too-short-refresh-key"));

        var exception = Assert.Throws<InvalidOperationException>(() => NhRefreshTokenKey.Digest(configuration, Token));

        Assert.Contains(NhRefreshTokenKey.ConfigurationKey, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("too-short-refresh-key", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void WithoutEitherKeyNoDigestIsComputed()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            NhRefreshTokenKey.Digest(Configuration(), Token));

        Assert.Contains(NhRefreshTokenKey.ConfigurationKey, exception.Message, StringComparison.Ordinal);
        Assert.Contains(NhJwtSigningKey.ConfigurationKey, exception.Message, StringComparison.Ordinal);
    }

    private static IConfiguration Configuration(params (string Key, string Value)[] settings)
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(setting =>
                new KeyValuePair<string, string?>(setting.Key, setting.Value)))
            .Build();
    }
}
