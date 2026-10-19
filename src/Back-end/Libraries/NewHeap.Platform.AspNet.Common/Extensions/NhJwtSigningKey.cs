using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;
using NewHeap.Platform.Common;

namespace NewHeap.Platform.AspNet.Common.Extensions;

/// <summary>
/// The key NewHeap signs and validates its access tokens with. It must be configured, may not be an
/// unresolved <c>${Secrets:…}</c> reference and must hold at least <see cref="MinimumBytes"/> bytes:
/// a missing secret would otherwise make the reference text, which anyone can read in appsettings,
/// the signing key. The platform refuses to start without a usable key.
/// </summary>
public static class NhJwtSigningKey
{
    /// <summary>The configuration key of the signing key.</summary>
    public const string ConfigurationKey = "NewHeap:PlatformAspNetCommon:Authorization:JWT:Token:Key";

    /// <summary>The shortest key HMAC-SHA256 accepts: 256 bits.</summary>
    public const int MinimumBytes = 32;

    /// <summary>
    /// Returns the configured signing key, from the key itself or from the secrets files
    /// (<see cref="NhConfigurationSecrets.Read"/>).
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The key is missing, still an unresolved reference, or shorter than <see cref="MinimumBytes"/> bytes.
    /// The message names the setting, never its value.
    /// </exception>
    public static string Read(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var key = NhConfigurationSecrets.Read(configuration, ConfigurationKey)
            ?? throw new InvalidOperationException(
                $"{ConfigurationKey} is not configured or is an unresolved ${{Secrets:...}} reference. " +
                $"Set a random key of at least {MinimumBytes} bytes, for example 64 random bytes in Base64, in the application's secrets file.");
        if (Encoding.UTF8.GetByteCount(key) < MinimumBytes)
        {
            throw new InvalidOperationException(
                $"{ConfigurationKey} must hold at least {MinimumBytes} bytes. Set a random key, for example 64 random bytes in Base64, in the application's secrets file.");
        }

        return key;
    }

    /// <summary>Creates the symmetric key for signing and validating tokens from <see cref="Read"/>.</summary>
    public static SymmetricSecurityKey Create(IConfiguration configuration)
    {
        return new SymmetricSecurityKey(Encoding.UTF8.GetBytes(Read(configuration)));
    }
}
