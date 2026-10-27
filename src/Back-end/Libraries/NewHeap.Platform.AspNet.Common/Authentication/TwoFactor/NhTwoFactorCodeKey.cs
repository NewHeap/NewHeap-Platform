using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;
using NewHeap.Platform.AspNet.Common.Extensions;
using NewHeap.Platform.Common;

namespace NewHeap.Platform.AspNet.Common.Authentication.TwoFactor;

/// <summary>
/// The key NewHeap stores e-mailed two-factor codes and recovery codes with. The Identity token table
/// holds only a keyed HMAC-SHA256 of each code, so a copy of the table cannot be tested against the few
/// possible six-digit codes, or against recovery codes, without the key. The key is
/// <see cref="ConfigurationKey"/> when it is configured, otherwise the refresh-token key
/// (<see cref="NhRefreshTokenKey.ConfigurationKey"/>), otherwise the shared <see cref="NhJwtSigningKey"/>;
/// each is read with <see cref="NhConfigurationSecrets.Read"/>, so it lives in the secrets file.
/// </summary>
/// <remarks>
/// New codes are stored with the first configured key. A presented code is checked against every
/// configured key, so configuring <see cref="ConfigurationKey"/> or a refresh-token key later keeps the
/// stored codes working for as long as the key they were stored with stays configured. Removing or
/// changing that key invalidates the recovery codes stored with it, and users regenerate them. Configure
/// <see cref="ConfigurationKey"/> before users enroll, so that rotating the signing or refresh-token key
/// leaves their recovery codes alone.
/// </remarks>
public static class NhTwoFactorCodeKey
{
    /// <summary>The configuration key of the two-factor code key, a secret; optional.</summary>
    public const string ConfigurationKey = "NewHeap:PlatformAspNetCommon:Authorization:TwoFactor:CodeKey";

    /// <summary>The shortest key accepted: 256 bits.</summary>
    public const int MinimumBytes = NhJwtSigningKey.MinimumBytes;

    /// <summary>The purpose recovery-code hashes are derived for.</summary>
    internal const string RecoveryCodePurpose = "NewHeap.Platform.AspNet.Common.TwoFactor.RecoveryCode.v1";

    /// <summary>The purpose e-mailed-code hashes are derived for.</summary>
    internal const string EmailCodePurpose = "NewHeap.Platform.AspNet.Common.TwoFactor.EmailCode.v1";

    private const int DerivedKeyBytes = 32;

    /// <summary>
    /// Returns a key derived (HKDF-SHA256) for <paramref name="purpose"/> from every configured key, the
    /// key new codes are stored with first. A key configured under several settings appears once.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// No key is configured, or a configured key is too short. The message names the setting, never its value.
    /// </exception>
    internal static IReadOnlyList<byte[]> DeriveKeys(IConfiguration configuration, string purpose)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(purpose);

        var secrets = new List<string>();
        AddConfiguredSecret(configuration, ConfigurationKey, secrets);
        AddConfiguredSecret(configuration, NhRefreshTokenKey.ConfigurationKey, secrets);
        if (NhConfigurationSecrets.Read(configuration, NhJwtSigningKey.ConfigurationKey) != null)
        {
            secrets.Add(NhJwtSigningKey.Read(configuration));
        }

        if (secrets.Count == 0)
        {
            throw new InvalidOperationException(
                $"Two-factor codes are stored with {ConfigurationKey}, or else with {NhRefreshTokenKey.ConfigurationKey} " +
                $"or {NhJwtSigningKey.ConfigurationKey}, and none is configured. Set a random key of at least " +
                $"{MinimumBytes} bytes, for example 64 random bytes in Base64, as {ConfigurationKey} in the application's secrets file.");
        }

        var info = Encoding.UTF8.GetBytes(purpose);
        return secrets
            .Distinct(StringComparer.Ordinal)
            .Select(secret => HKDF.DeriveKey(
                HashAlgorithmName.SHA256,
                Encoding.UTF8.GetBytes(secret),
                DerivedKeyBytes,
                salt: [],
                info: info))
            .ToList();
    }

    /// <summary>Throws the configuration error <see cref="DeriveKeys"/> would throw, so startup can report it.</summary>
    internal static void Validate(IConfiguration configuration)
    {
        DeriveKeys(configuration, RecoveryCodePurpose);
    }

    private static void AddConfiguredSecret(IConfiguration configuration, string key, List<string> secrets)
    {
        var secret = NhConfigurationSecrets.Read(configuration, key);
        if (secret == null)
        {
            return;
        }

        if (Encoding.UTF8.GetByteCount(secret) < MinimumBytes)
        {
            throw new InvalidOperationException(
                $"{key} must hold at least {MinimumBytes} bytes. Set a random key, for example 64 random bytes in Base64, in the application's secrets file.");
        }

        secrets.Add(secret);
    }
}
