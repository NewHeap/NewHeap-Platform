using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;
using NewHeap.Platform.Common;

namespace NewHeap.Platform.AspNet.Common.Extensions;

/// <summary>
/// The key NewHeap stores refresh tokens with. A refresh-token row holds only a keyed HMAC-SHA256 digest
/// of the token, and only the client receives the token itself, so a copy of the token table cannot be
/// presented as a refresh token. The key is <see cref="ConfigurationKey"/> when it is configured,
/// otherwise the shared <see cref="NhJwtSigningKey"/>; both are read with
/// <see cref="NhConfigurationSecrets.Read"/>, so they live in the secrets file.
/// </summary>
/// <remarks>
/// Configure <see cref="ConfigurationKey"/> when the application signs its access tokens asymmetrically
/// (<see cref="NhJwtSigningKeys"/>) without a shared key, or to rotate the shared signing key without
/// ending refresh sessions. Changing the key in use ends every refresh session: the stored digests no
/// longer match, and the users sign in again.
/// </remarks>
public static class NhRefreshTokenKey
{
    /// <summary>The configuration key of the refresh-token key, a secret; optional.</summary>
    public const string ConfigurationKey = "NewHeap:PlatformAspNetCommon:Authorization:RefreshToken:Key";

    /// <summary>The shortest key accepted: 256 bits.</summary>
    public const int MinimumBytes = NhJwtSigningKey.MinimumBytes;

    /// <summary>
    /// Marks a stored digest and its version. Refresh tokens are Base64 and never contain <c>:</c>, so
    /// a row stored before digests existed never starts with it.
    /// </summary>
    internal const string DigestPrefix = "nhr1:";

    private const int DerivedKeyBytes = 32;

    private static readonly byte[] DerivationPurpose =
        Encoding.UTF8.GetBytes("NewHeap.Platform.AspNet.Common.RefreshToken.v1");

    /// <summary>
    /// Returns the value a refresh-token row stores for <paramref name="refreshToken"/>: the version
    /// prefix and the lowercase hexadecimal HMAC-SHA256 of the token under a key derived (HKDF-SHA256)
    /// from the configured key.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Neither key is configured, or the configured key is too short. The message names the setting,
    /// never its value.
    /// </exception>
    internal static string Digest(IConfiguration configuration, string refreshToken)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(refreshToken);

        var key = DeriveKey(configuration);
        var digest = HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(refreshToken));

        return DigestPrefix + Convert.ToHexStringLower(digest);
    }

    private static byte[] DeriveKey(IConfiguration configuration)
    {
        var secret = NhConfigurationSecrets.Read(configuration, ConfigurationKey);
        if (secret != null)
        {
            if (Encoding.UTF8.GetByteCount(secret) < MinimumBytes)
            {
                throw new InvalidOperationException(
                    $"{ConfigurationKey} must hold at least {MinimumBytes} bytes. Set a random key, for example 64 random bytes in Base64, in the application's secrets file.");
            }
        }
        else if (NhConfigurationSecrets.Read(configuration, NhJwtSigningKey.ConfigurationKey) != null)
        {
            secret = NhJwtSigningKey.Read(configuration);
        }
        else
        {
            throw new InvalidOperationException(
                $"Refresh tokens are stored with {ConfigurationKey}, or else with {NhJwtSigningKey.ConfigurationKey}, and neither is configured. " +
                $"Set a random key of at least {MinimumBytes} bytes, for example 64 random bytes in Base64, in the application's secrets file.");
        }

        return HKDF.DeriveKey(
            HashAlgorithmName.SHA256,
            Encoding.UTF8.GetBytes(secret),
            DerivedKeyBytes,
            salt: [],
            info: DerivationPurpose);
    }
}
