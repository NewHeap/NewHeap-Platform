using Microsoft.Extensions.Configuration;
using NewHeap.Platform.AspNet.Common.Authentication.TwoFactor;
using NewHeap.Platform.AspNet.Common.Extensions;
using Xunit;

namespace NewHeap.Platform.AspNet.Common.Tests;

public sealed class NhTwoFactorCodeKeyTests
{
    private const string SigningKey = "test-code-key-tests-signing-key-of-32-bytes";
    private const string RefreshKey = "test-code-key-tests-refresh-key-of-32-bytes";
    private const string CodeKey = "test-code-key-tests-code-key-of-thirty-two-bytes";
    private const string Code = "ABCDE-FGHIJ";

    [Fact]
    public void TheCodeKeyComesFirstThenTheRefreshKeyThenTheSigningKey()
    {
        var all = Keys(
            (NhJwtSigningKey.ConfigurationKey, SigningKey),
            (NhRefreshTokenKey.ConfigurationKey, RefreshKey),
            (NhTwoFactorCodeKey.ConfigurationKey, CodeKey));

        Assert.Equal(3, all.Count);
        Assert.Equal(Keys((NhTwoFactorCodeKey.ConfigurationKey, CodeKey))[0], all[0]);
        Assert.Equal(Keys((NhRefreshTokenKey.ConfigurationKey, RefreshKey))[0], all[1]);
        Assert.Equal(Keys((NhJwtSigningKey.ConfigurationKey, SigningKey))[0], all[2]);
        Assert.Equal(32, all[0].Length);
    }

    [Fact]
    public void EachPurposeAndKeyGivesAnotherHash()
    {
        var configuration = Configuration((NhTwoFactorCodeKey.ConfigurationKey, CodeKey));
        var recoveryKey = NhTwoFactorCodeKey.DeriveKeys(configuration, NhTwoFactorCodeKey.RecoveryCodePurpose)[0];
        var emailKey = NhTwoFactorCodeKey.DeriveKeys(configuration, NhTwoFactorCodeKey.EmailCodePurpose)[0];
        var otherKey = Keys((NhTwoFactorCodeKey.ConfigurationKey, CodeKey + "x"))[0];

        Assert.NotEqual(recoveryKey, emailKey);
        Assert.NotEqual(
            NhRecoveryCodeHasher.Hash(recoveryKey, Code),
            NhRecoveryCodeHasher.Hash(otherKey, Code));
        Assert.Equal(
            NhRecoveryCodeHasher.Hash(recoveryKey, Code),
            NhRecoveryCodeHasher.Hash(Keys((NhTwoFactorCodeKey.ConfigurationKey, CodeKey))[0], Code));
    }

    [Fact]
    public void AKeyConfiguredTwiceIsDerivedOnce()
    {
        var keys = Keys(
            (NhJwtSigningKey.ConfigurationKey, SigningKey),
            (NhTwoFactorCodeKey.ConfigurationKey, SigningKey));

        Assert.Single(keys);
    }

    [Fact]
    public void TheCodeKeyIsReadFromTheSecretsFiles()
    {
        var fromSecrets = Keys(
            (NhTwoFactorCodeKey.ConfigurationKey, "${Secrets:" + NhTwoFactorCodeKey.ConfigurationKey + "}"),
            ("Secrets:" + NhTwoFactorCodeKey.ConfigurationKey, CodeKey));

        Assert.Equal(Keys((NhTwoFactorCodeKey.ConfigurationKey, CodeKey)), fromSecrets);
    }

    [Fact]
    public void AShortCodeKeyIsRefusedWithoutItsValue()
    {
        var configuration = Configuration(
            (NhJwtSigningKey.ConfigurationKey, SigningKey),
            (NhTwoFactorCodeKey.ConfigurationKey, "test-short-code-key"));

        var exception = Assert.Throws<InvalidOperationException>(() => NhTwoFactorCodeKey.Validate(configuration));

        Assert.Contains(NhTwoFactorCodeKey.ConfigurationKey, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("test-short-code-key", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void WithoutAnyKeyNoCodeIsHashed()
    {
        var exception = Assert.Throws<InvalidOperationException>(() => NhTwoFactorCodeKey.Validate(Configuration()));

        Assert.Contains(NhTwoFactorCodeKey.ConfigurationKey, exception.Message, StringComparison.Ordinal);
        Assert.Contains(NhRefreshTokenKey.ConfigurationKey, exception.Message, StringComparison.Ordinal);
        Assert.Contains(NhJwtSigningKey.ConfigurationKey, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EarlierRecoveryCodeEntriesAreRehashedWithoutTheCode()
    {
        var key = Keys((NhTwoFactorCodeKey.ConfigurationKey, CodeKey))[0];
        var keyed = NhRecoveryCodeHasher.Hash(key, Code);

        // An unkeyed hash and a plaintext code from before NewHeap hashed codes become the keyed entry.
        Assert.Equal(keyed, NhRecoveryCodeHasher.Rehash(key, NhRecoveryCodeHasher.UnkeyedHash(Code)));
        Assert.Equal(keyed, NhRecoveryCodeHasher.Rehash(key, "abcde-fghij"));

        // A keyed entry, an empty entry and an unknown entry stay as they are.
        Assert.Null(NhRecoveryCodeHasher.Rehash(key, keyed));
        Assert.Null(NhRecoveryCodeHasher.Rehash(key, string.Empty));
        Assert.Null(NhRecoveryCodeHasher.Rehash(key, "nhv1:not-a-digest"));
        Assert.Null(NhRecoveryCodeHasher.Rehash(key, "other:entry"));
    }

    private static IReadOnlyList<byte[]> Keys(params (string Key, string Value)[] settings)
    {
        return NhTwoFactorCodeKey.DeriveKeys(Configuration(settings), NhTwoFactorCodeKey.RecoveryCodePurpose);
    }

    private static IConfiguration Configuration(params (string Key, string Value)[] settings)
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(setting =>
                new KeyValuePair<string, string?>(setting.Key, setting.Value)))
            .Build();
    }
}
