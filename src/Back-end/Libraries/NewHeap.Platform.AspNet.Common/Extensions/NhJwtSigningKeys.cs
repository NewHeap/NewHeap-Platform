using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;
using NewHeap.Platform.Common;

namespace NewHeap.Platform.AspNet.Common.Extensions;

/// <summary>
/// The algorithms NewHeap signs its access tokens with: <see cref="HS256"/> with the shared
/// <see cref="NhJwtSigningKey"/> (the default), or <see cref="RS256"/> or <see cref="ES256"/> with a
/// private key only the issuing host holds.
/// </summary>
public static class NhJwtSigningAlgorithms
{
    /// <summary>HMAC-SHA256 with the shared key: every host that validates a token can also sign one.</summary>
    public const string HS256 = SecurityAlgorithms.HmacSha256;

    /// <summary>RSASSA-PKCS1-v1_5 with SHA-256 and an RSA key of at least 2048 bits.</summary>
    public const string RS256 = SecurityAlgorithms.RsaSha256;

    /// <summary>ECDSA with the P-256 curve and SHA-256.</summary>
    public const string ES256 = SecurityAlgorithms.EcdsaSha256;
}

/// <summary>
/// Reads how NewHeap signs and validates its access tokens. Without the <see cref="Section"/> section,
/// or with <c>Algorithm</c> <see cref="NhJwtSigningAlgorithms.HS256"/>, tokens are signed with the
/// shared <see cref="NhJwtSigningKey"/> exactly as before. With <see cref="NhJwtSigningAlgorithms.RS256"/>
/// or <see cref="NhJwtSigningAlgorithms.ES256"/> they are signed with a private key that only the
/// issuing host holds: <c>PrivateKey</c> (PEM) or <c>Pkcs12</c> (Base64, with an optional
/// <c>Pkcs12Password</c>), both read with <see cref="NhConfigurationSecrets.Read"/> so they live in the
/// secrets file, and a <c>KeyId</c>. Other hosts validate with the public keys that
/// <see cref="NhJwksEndpointRouteBuilderExtensions.MapNewHeapJwks"/> publishes, so they cannot mint
/// tokens. <c>PreviousKeys</c> (<c>KeyId</c> and <c>PublicKey</c>, a PEM public key or certificate)
/// keep validating, and are published, while tokens of a rotated key are still valid.
/// <c>AcceptSharedKeyTokens</c> lets the issuing host accept tokens of its former shared key while an
/// application moves from HS256. A configuration error throws <see cref="InvalidOperationException"/>
/// that names the setting, never its value, so the platform does not start with it. Keys are read
/// once per configuration and read again when their settings change.
/// </summary>
public static partial class NhJwtSigningKeys
{
    /// <summary>The configuration section of the signing key.</summary>
    public const string Section = "NewHeap:PlatformAspNetCommon:Authorization:JWT:Token:Signing";

    /// <summary>One of <see cref="NhJwtSigningAlgorithms"/>; <see cref="NhJwtSigningAlgorithms.HS256"/> when missing.</summary>
    public const string AlgorithmKey = Section + ":Algorithm";

    /// <summary>The key id (<c>kid</c>) of the current private key; required for RS256 and ES256.</summary>
    public const string KeyIdKey = Section + ":KeyId";

    /// <summary>The current private key as PEM (PKCS#8, or a PKCS#1 RSA or SEC1 EC key), a secret.</summary>
    public const string PrivateKeyKey = Section + ":PrivateKey";

    /// <summary>The current private key as a Base64 PKCS#12 file with its certificate, a secret.</summary>
    public const string Pkcs12Key = Section + ":Pkcs12";

    /// <summary>The password of <see cref="Pkcs12Key"/>, a secret; optional.</summary>
    public const string Pkcs12PasswordKey = Section + ":Pkcs12Password";

    /// <summary>The keys that still validate tokens: entries with <c>KeyId</c> and <c>PublicKey</c>.</summary>
    public const string PreviousKeysSection = Section + ":PreviousKeys";

    /// <summary>
    /// Whether an application that signs asymmetrically still accepts tokens of its shared
    /// <see cref="NhJwtSigningKey"/>, while it moves from HS256; <see langword="false"/> by default.
    /// </summary>
    public const string AcceptSharedKeyTokensKey = Section + ":AcceptSharedKeyTokens";

    /// <summary>The default path of the key set (<see cref="NhJwksEndpointRouteBuilderExtensions.MapNewHeapJwks"/>).</summary>
    public const string DefaultJwksPath = "/.well-known/jwks.json";

    /// <summary>The smallest RSA key RS256 accepts.</summary>
    public const int MinimumRsaKeySize = 2048;

    private static readonly ConditionalWeakTable<IConfiguration, CachedKeySet> Cache = new();

    /// <summary>
    /// Whether the configuration asks for asymmetric signing (RS256 or ES256), without reading the keys.
    /// </summary>
    public static bool IsAsymmetric(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        return ParseAlgorithm(configuration[AlgorithmKey]) != NhJwtSigningAlgorithms.HS256;
    }

    /// <summary>
    /// Returns the configured keys: the shared HS256 key by default, or the asymmetric key with its
    /// previous keys. The same instance is returned until the relevant settings change.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The signing configuration is missing, incomplete or invalid. The message names the setting,
    /// never its value.
    /// </exception>
    public static NhJwtSigningKeySet Read(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var fingerprint = Fingerprint(configuration);
        if (Cache.TryGetValue(configuration, out var cached) && cached.Fingerprint == fingerprint)
        {
            return cached.Keys;
        }

        var keys = Create(configuration);
        Cache.AddOrUpdate(configuration, new CachedKeySet(fingerprint, keys));
        return keys;
    }

    private static NhJwtSigningKeySet Create(IConfiguration configuration)
    {
        var algorithm = ParseAlgorithm(configuration[AlgorithmKey]);
        if (algorithm == NhJwtSigningAlgorithms.HS256)
        {
            // The shared key, exactly as before asymmetric signing existed.
            var shared = NhJwtSigningKey.Create(configuration);
            return new NhJwtSigningKeySet(
                NhJwtSigningAlgorithms.HS256,
                null,
                new SigningCredentials(shared, SecurityAlgorithms.HmacSha256),
                [shared],
                [],
                []);
        }

        var keyId = NhConfigurationSecrets.Read(configuration, KeyIdKey);
        if (keyId is null || !KeyIdPattern().IsMatch(keyId))
        {
            throw new InvalidOperationException(
                $"{KeyIdKey} must name the current signing key with 1 to 64 letters, digits, '.', '_' or '-' when {AlgorithmKey} is {algorithm}.");
        }

        var current = ReadPrivateKey(configuration, algorithm, keyId);
        var publicKeys = new List<NhJwtPublicKey> { current.PublicKey };
        foreach (var previous in configuration.GetSection(PreviousKeysSection).GetChildren())
        {
            var previousKey = ReadPublicKey(configuration, previous);
            if (publicKeys.Any(key => string.Equals(key.KeyId, previousKey.KeyId, StringComparison.Ordinal)))
            {
                throw new InvalidOperationException(
                    $"{previous.Path}:KeyId repeats the key id of another signing key; every key needs its own id.");
            }
            publicKeys.Add(previousKey);
        }

        var validationKeys = publicKeys.Select(key => key.SecurityKey).ToList();
        var validAlgorithms = publicKeys.Select(key => key.Algorithm).Distinct(StringComparer.Ordinal).ToList();
        if (string.Equals(configuration[AcceptSharedKeyTokensKey], bool.TrueString, StringComparison.OrdinalIgnoreCase))
        {
            // Only while an application moves from HS256: the shared key is never published.
            validationKeys.Add(NhJwtSigningKey.Create(configuration));
            validAlgorithms.Add(NhJwtSigningAlgorithms.HS256);
        }

        return new NhJwtSigningKeySet(
            algorithm,
            keyId,
            new SigningCredentials(current.PrivateKey, algorithm),
            validationKeys,
            validAlgorithms,
            publicKeys);
    }

    private static string ParseAlgorithm(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || string.Equals(value, NhJwtSigningAlgorithms.HS256, StringComparison.OrdinalIgnoreCase))
        {
            return NhJwtSigningAlgorithms.HS256;
        }
        if (string.Equals(value, NhJwtSigningAlgorithms.RS256, StringComparison.OrdinalIgnoreCase))
        {
            return NhJwtSigningAlgorithms.RS256;
        }
        if (string.Equals(value, NhJwtSigningAlgorithms.ES256, StringComparison.OrdinalIgnoreCase))
        {
            return NhJwtSigningAlgorithms.ES256;
        }

        throw new InvalidOperationException($"{AlgorithmKey} must be HS256, RS256 or ES256.");
    }

    private static (AsymmetricSecurityKey PrivateKey, NhJwtPublicKey PublicKey) ReadPrivateKey(
        IConfiguration configuration,
        string algorithm,
        string keyId)
    {
        var pem = NhConfigurationSecrets.Read(configuration, PrivateKeyKey);
        var pkcs12 = NhConfigurationSecrets.Read(configuration, Pkcs12Key);
        if ((pem is null) == (pkcs12 is null))
        {
            throw new InvalidOperationException(
                $"Configure exactly one of {PrivateKeyKey} (PEM) and {Pkcs12Key} (Base64 PKCS#12) in the application's secrets file when {AlgorithmKey} is {algorithm}.");
        }

        AsymmetricAlgorithm key;
        string setting;
        if (pem is not null)
        {
            setting = PrivateKeyKey;
            key = ImportPrivatePem(pem, algorithm, setting);
        }
        else
        {
            setting = Pkcs12Key;
            key = ImportPkcs12(pkcs12!, NhConfigurationSecrets.Read(configuration, Pkcs12PasswordKey), algorithm, setting);
        }

        RequireFits(key, algorithm, setting);
        AsymmetricSecurityKey privateKey = key switch
        {
            RSA rsa => new RsaSecurityKey(rsa) { KeyId = keyId },
            ECDsa ecdsa => new ECDsaSecurityKey(ecdsa) { KeyId = keyId },
            _ => throw new InvalidOperationException($"{setting} holds no RSA or EC key.")
        };
        return (privateKey, PublicOf(key, keyId, algorithm));
    }

    private static AsymmetricAlgorithm ImportPrivatePem(string pem, string algorithm, string setting)
    {
        AsymmetricAlgorithm key = algorithm == NhJwtSigningAlgorithms.RS256 ? RSA.Create() : ECDsa.Create();
        try
        {
            switch (key)
            {
                case RSA rsa:
                    rsa.ImportFromPem(pem);
                    // A PEM public key imports without error but cannot sign.
                    rsa.ExportParameters(includePrivateParameters: true);
                    break;
                case ECDsa ecdsa:
                    ecdsa.ImportFromPem(pem);
                    ecdsa.ExportParameters(includePrivateParameters: true);
                    break;
            }
        }
        catch (Exception exception) when (exception is CryptographicException or ArgumentException)
        {
            key.Dispose();
            throw new InvalidOperationException(
                $"{setting} must hold a PEM private key that fits {algorithm} (an RSA key for RS256, a P-256 EC key for ES256).");
        }

        return key;
    }

    private static AsymmetricAlgorithm ImportPkcs12(string base64, string? password, string algorithm, string setting)
    {
        try
        {
            using var certificate = X509CertificateLoader.LoadPkcs12(
                Convert.FromBase64String(base64),
                password,
                OperatingSystem.IsMacOS() ? X509KeyStorageFlags.DefaultKeySet : X509KeyStorageFlags.EphemeralKeySet);
            AsymmetricAlgorithm? key = algorithm == NhJwtSigningAlgorithms.RS256
                ? certificate.GetRSAPrivateKey()
                : certificate.GetECDsaPrivateKey();
            return key ?? throw new InvalidOperationException(
                $"{setting} must hold a certificate with a private key that fits {algorithm} (an RSA key for RS256, a P-256 EC key for ES256).");
        }
        catch (Exception exception) when (exception is CryptographicException or FormatException)
        {
            throw new InvalidOperationException(
                $"{setting} must hold a Base64 PKCS#12 file that {Pkcs12PasswordKey} opens.");
        }
    }

    private static NhJwtPublicKey ReadPublicKey(IConfiguration configuration, IConfigurationSection entry)
    {
        var keyId = NhConfigurationSecrets.Read(configuration, entry.Path + ":KeyId");
        if (keyId is null || !KeyIdPattern().IsMatch(keyId))
        {
            throw new InvalidOperationException(
                $"{entry.Path}:KeyId must name the previous key with 1 to 64 letters, digits, '.', '_' or '-'.");
        }

        var setting = entry.Path + ":PublicKey";
        var pem = NhConfigurationSecrets.Read(configuration, setting)
            ?? throw new InvalidOperationException($"{setting} must hold the previous key as a PEM public key or certificate.");
        AsymmetricAlgorithm key;
        try
        {
            key = pem.Contains("CERTIFICATE-----", StringComparison.Ordinal)
                ? FromCertificate(pem)
                : FromPublicPem(pem);
        }
        catch (Exception exception) when (exception is CryptographicException or ArgumentException)
        {
            throw new InvalidOperationException($"{setting} must hold the previous key as a PEM public key or certificate.");
        }

        var algorithm = key is RSA ? NhJwtSigningAlgorithms.RS256 : NhJwtSigningAlgorithms.ES256;
        RequireFits(key, algorithm, setting);
        return PublicOf(key, keyId, algorithm);
    }

    private static AsymmetricAlgorithm FromCertificate(string pem)
    {
        using var certificate = X509Certificate2.CreateFromPem(pem);
        return (AsymmetricAlgorithm?)certificate.GetRSAPublicKey()
            ?? certificate.GetECDsaPublicKey()
            ?? throw new CryptographicException("The certificate holds no RSA or EC key.");
    }

    private static AsymmetricAlgorithm FromPublicPem(string pem)
    {
        var rsa = RSA.Create();
        try
        {
            rsa.ImportFromPem(pem);
            return rsa;
        }
        catch (Exception exception) when (exception is CryptographicException or ArgumentException)
        {
            rsa.Dispose();
        }

        var ecdsa = ECDsa.Create();
        try
        {
            ecdsa.ImportFromPem(pem);
            return ecdsa;
        }
        catch
        {
            ecdsa.Dispose();
            throw;
        }
    }

    private static void RequireFits(AsymmetricAlgorithm key, string algorithm, string setting)
    {
        var fits = algorithm switch
        {
            NhJwtSigningAlgorithms.RS256 => key is RSA { KeySize: >= MinimumRsaKeySize },
            NhJwtSigningAlgorithms.ES256 => key is ECDsa ecdsa && IsP256(ecdsa),
            _ => false
        };
        if (!fits)
        {
            key.Dispose();
            throw new InvalidOperationException(
                $"{setting} must hold a key that fits {algorithm}: an RSA key of at least {MinimumRsaKeySize} bits for RS256, a P-256 EC key for ES256.");
        }
    }

    private static bool IsP256(ECDsa ecdsa)
    {
        var curve = ecdsa.ExportParameters(includePrivateParameters: false).Curve;
        return curve.IsNamed
            && (string.Equals(curve.Oid.Value, ECCurve.NamedCurves.nistP256.Oid.Value, StringComparison.Ordinal)
                || string.Equals(curve.Oid.FriendlyName, ECCurve.NamedCurves.nistP256.Oid.FriendlyName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(curve.Oid.FriendlyName, "nistP256", StringComparison.OrdinalIgnoreCase)
                || string.Equals(curve.Oid.FriendlyName, "ECDSA_P256", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The public part of a key only, so a published or validating key can never sign.</summary>
    private static NhJwtPublicKey PublicOf(AsymmetricAlgorithm key, string keyId, string algorithm)
    {
        switch (key)
        {
            case RSA rsa:
            {
                var parameters = rsa.ExportParameters(includePrivateParameters: false);
                var publicRsa = RSA.Create(parameters);
                var jwk = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["kty"] = "RSA",
                    ["use"] = "sig",
                    ["kid"] = keyId,
                    ["alg"] = algorithm,
                    ["n"] = Base64UrlEncoder.Encode(parameters.Modulus!),
                    ["e"] = Base64UrlEncoder.Encode(parameters.Exponent!)
                };
                return new NhJwtPublicKey(keyId, algorithm, new RsaSecurityKey(publicRsa) { KeyId = keyId }, jwk);
            }
            case ECDsa ecdsa:
            {
                var parameters = ecdsa.ExportParameters(includePrivateParameters: false);
                var publicEcdsa = ECDsa.Create(new ECParameters { Curve = ECCurve.NamedCurves.nistP256, Q = parameters.Q });
                var jwk = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["kty"] = "EC",
                    ["use"] = "sig",
                    ["kid"] = keyId,
                    ["alg"] = algorithm,
                    ["crv"] = "P-256",
                    ["x"] = Base64UrlEncoder.Encode(parameters.Q.X!),
                    ["y"] = Base64UrlEncoder.Encode(parameters.Q.Y!)
                };
                return new NhJwtPublicKey(keyId, algorithm, new ECDsaSecurityKey(publicEcdsa) { KeyId = keyId }, jwk);
            }
            default:
                throw new InvalidOperationException("Only RSA and EC keys sign NewHeap tokens.");
        }
    }

    /// <summary>
    /// A hash of the settings that make up the keys, so a changed configuration is read again; the
    /// values themselves are not kept.
    /// </summary>
    private static string Fingerprint(IConfiguration configuration)
    {
        var builder = new StringBuilder();
        foreach (var key in new[]
        {
            NhJwtSigningKey.ConfigurationKey,
            AlgorithmKey,
            KeyIdKey,
            PrivateKeyKey,
            Pkcs12Key,
            Pkcs12PasswordKey,
            AcceptSharedKeyTokensKey
        })
        {
            builder.Append(configuration[key]).Append('\n')
                .Append(configuration[NhConfigurationSecrets.Prefix + ":" + key]).Append('\n');
        }
        foreach (var previous in configuration.GetSection(PreviousKeysSection).GetChildren())
        {
            builder.Append(previous["KeyId"]).Append('\n')
                .Append(previous["PublicKey"]).Append('\n');
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }

    private sealed record CachedKeySet(string Fingerprint, NhJwtSigningKeySet Keys);

    [GeneratedRegex("^[A-Za-z0-9._-]{1,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex KeyIdPattern();
}

/// <summary>
/// The public key of an asymmetric signing key: its id, algorithm, validation key and JSON Web Key.
/// </summary>
internal sealed record NhJwtPublicKey(
    string KeyId,
    string Algorithm,
    SecurityKey SecurityKey,
    IReadOnlyDictionary<string, string> JsonWebKey);

/// <summary>
/// The keys of <see cref="NhJwtSigningKeys"/>: the credentials new tokens are signed with, the keys
/// tokens are validated with and, for asymmetric signing, the public key set other hosts validate
/// with.
/// </summary>
public sealed class NhJwtSigningKeySet
{
    private readonly IReadOnlyList<NhJwtPublicKey> _publicKeys;

    internal NhJwtSigningKeySet(
        string algorithm,
        string? keyId,
        SigningCredentials signingCredentials,
        IReadOnlyList<SecurityKey> validationKeys,
        IReadOnlyList<string> validAlgorithms,
        IReadOnlyList<NhJwtPublicKey> publicKeys)
    {
        Algorithm = algorithm;
        KeyId = keyId;
        SigningCredentials = signingCredentials;
        ValidationKeys = validationKeys;
        ValidAlgorithms = validAlgorithms;
        _publicKeys = publicKeys;
    }

    /// <summary>The algorithm new tokens are signed with (<see cref="NhJwtSigningAlgorithms"/>).</summary>
    public string Algorithm { get; }

    /// <summary>Whether tokens are signed with a private key (RS256 or ES256) instead of the shared key.</summary>
    public bool IsAsymmetric => Algorithm != NhJwtSigningAlgorithms.HS256;

    /// <summary>The key id (<c>kid</c>) of the current key; <see langword="null"/> for HS256.</summary>
    public string? KeyId { get; }

    /// <summary>The credentials new tokens are signed with.</summary>
    public SigningCredentials SigningCredentials { get; }

    /// <summary>
    /// The keys tokens are validated with: the shared key for HS256; otherwise the public keys of the
    /// current and previous keys, and the shared key only with <c>AcceptSharedKeyTokens</c>.
    /// </summary>
    public IReadOnlyList<SecurityKey> ValidationKeys { get; }

    /// <summary>The algorithms a validated token may use; empty for HS256, which keeps the defaults.</summary>
    public IReadOnlyList<string> ValidAlgorithms { get; }

    /// <summary>The key ids of the published public keys, the current one first; empty for HS256.</summary>
    public IReadOnlyList<string> PublishedKeyIds => _publicKeys.Select(key => key.KeyId).ToArray();

    /// <summary>
    /// Applies the keys to token validation parameters. HS256 sets <see cref="TokenValidationParameters.IssuerSigningKey"/>
    /// to the shared key, as before asymmetric signing existed. RS256 and ES256 set the current public
    /// key, every validation key and <see cref="TokenValidationParameters.ValidAlgorithms"/>.
    /// </summary>
    public void ApplyTo(TokenValidationParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);

        if (!IsAsymmetric)
        {
            parameters.IssuerSigningKey = ValidationKeys[0];
            return;
        }

        parameters.IssuerSigningKey = _publicKeys[0].SecurityKey;
        parameters.IssuerSigningKeys = ValidationKeys;
        parameters.ValidAlgorithms = ValidAlgorithms;
    }

    /// <summary>
    /// The JSON Web Key Set (RFC 7517) of the public keys, the current key first: <c>kty</c>,
    /// <c>use</c>, <c>kid</c>, <c>alg</c> and the public parameters only.
    /// </summary>
    /// <exception cref="InvalidOperationException">The keys are the shared HS256 key, which is never published.</exception>
    public string ToJwksJson()
    {
        if (!IsAsymmetric)
        {
            throw new InvalidOperationException(
                $"The application signs its tokens with the shared HS256 key, which is never published; set {NhJwtSigningKeys.AlgorithmKey} to RS256 or ES256 to publish a key set.");
        }

        return JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["keys"] = _publicKeys.Select(key => key.JsonWebKey).ToArray()
        });
    }
}
