using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace NewHeap.Platform.AI.Chat.Notifications;

/// <summary>
/// Message encryption for Web Push (RFC 8291) in the <c>aes128gcm</c> content coding (RFC 8188):
/// one record with a fresh ephemeral key pair and salt per message.
/// </summary>
internal static class NhAssistantWebPushEncryption
{
    /// <summary>
    /// The record size written into the header. Push services accept bodies up to 4,096 bytes.
    /// </summary>
    public const int RecordSize = 4_096;

    /// <summary>
    /// Header: salt (16), record size (4), key id length (1) and the 65-byte server public key.
    /// </summary>
    public const int HeaderLength = 86;

    /// <summary>
    /// The largest payload that still fits one record of a 4,096-byte body.
    /// </summary>
    public const int MaxPlaintextLength = RecordSize - HeaderLength - 16 - 1;

    private static readonly byte[] KeyInfoPrefix = Encoding.ASCII.GetBytes("WebPush: info\0");
    private static readonly byte[] ContentEncryptionKeyInfo = Encoding.ASCII.GetBytes("Content-Encoding: aes128gcm\0");
    private static readonly byte[] NonceInfo = Encoding.ASCII.GetBytes("Content-Encoding: nonce\0");

    public static byte[] Encrypt(ReadOnlySpan<byte> plaintext, byte[] userAgentPublicKey, byte[] authSecret)
    {
        using var serverKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        return Encrypt(plaintext, userAgentPublicKey, authSecret, serverKey, RandomNumberGenerator.GetBytes(16));
    }

    /// <summary>
    /// Encrypts with a given server key and salt. Only tests pass fixed values (RFC 8291 Appendix A).
    /// </summary>
    internal static byte[] Encrypt(
        ReadOnlySpan<byte> plaintext,
        byte[] userAgentPublicKey,
        byte[] authSecret,
        ECDiffieHellman serverKey,
        byte[] salt)
    {
        ArgumentNullException.ThrowIfNull(userAgentPublicKey);
        ArgumentNullException.ThrowIfNull(authSecret);
        ArgumentNullException.ThrowIfNull(serverKey);
        ArgumentNullException.ThrowIfNull(salt);
        if (!IsValidPublicKey(userAgentPublicKey) || authSecret.Length != 16 || salt.Length != 16)
        {
            throw new ArgumentException("The push subscription keys are invalid.");
        }
        if (plaintext.Length > MaxPlaintextLength)
        {
            throw new ArgumentOutOfRangeException(nameof(plaintext), "The push message is too large.");
        }

        using var userAgentKey = ECDiffieHellman.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint { X = userAgentPublicKey[1..33], Y = userAgentPublicKey[33..65] }
        });
        var serverPublicKey = NhAssistantWebPushKeys.UncompressedPoint(serverKey.ExportParameters(false).Q);
        var sharedSecret = serverKey.DeriveRawSecretAgreement(userAgentKey.PublicKey);

        var keyInfo = new byte[KeyInfoPrefix.Length + 65 + 65];
        KeyInfoPrefix.CopyTo(keyInfo, 0);
        userAgentPublicKey.CopyTo(keyInfo, KeyInfoPrefix.Length);
        serverPublicKey.CopyTo(keyInfo, KeyInfoPrefix.Length + 65);
        var inputKeyingMaterial = HKDF.DeriveKey(HashAlgorithmName.SHA256, sharedSecret, 32, authSecret, keyInfo);
        var pseudorandomKey = HKDF.Extract(HashAlgorithmName.SHA256, inputKeyingMaterial, salt);
        var contentEncryptionKey = HKDF.Expand(HashAlgorithmName.SHA256, pseudorandomKey, 16, ContentEncryptionKeyInfo);
        var nonce = HKDF.Expand(HashAlgorithmName.SHA256, pseudorandomKey, 12, NonceInfo);

        // One record: the plaintext followed by the last-record delimiter 0x02 and no padding.
        var record = new byte[plaintext.Length + 1];
        plaintext.CopyTo(record);
        record[^1] = 0x02;

        var body = new byte[HeaderLength + record.Length + 16];
        salt.CopyTo(body, 0);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(16, 4), RecordSize);
        body[20] = 65;
        serverPublicKey.CopyTo(body, 21);
        using var aes = new AesGcm(contentEncryptionKey, 16);
        aes.Encrypt(
            nonce,
            record,
            body.AsSpan(HeaderLength, record.Length),
            body.AsSpan(HeaderLength + record.Length, 16));

        CryptographicOperations.ZeroMemory(sharedSecret);
        CryptographicOperations.ZeroMemory(inputKeyingMaterial);
        CryptographicOperations.ZeroMemory(pseudorandomKey);
        CryptographicOperations.ZeroMemory(contentEncryptionKey);
        return body;
    }

    /// <summary>
    /// True for an uncompressed point on P-256.
    /// </summary>
    public static bool IsValidPublicKey(byte[] key)
    {
        if (key.Length != 65 || key[0] != 0x04)
        {
            return false;
        }
        try
        {
            using var imported = ECDiffieHellman.Create(new ECParameters
            {
                Curve = ECCurve.NamedCurves.nistP256,
                Q = new ECPoint { X = key[1..33], Y = key[33..65] }
            });
            return true;
        }
        catch (CryptographicException)
        {
            return false;
        }
    }
}

/// <summary>
/// VAPID authorization (RFC 8292): a short-lived ES256 JWT for the push service origin.
/// </summary>
internal static class NhAssistantVapid
{
    private static readonly string Header = NhAssistantBase64Url.Encode(
        Encoding.UTF8.GetBytes("{\"typ\":\"JWT\",\"alg\":\"ES256\"}"));

    /// <summary>
    /// The <c>Authorization</c> header value for one push service origin.
    /// </summary>
    public static string CreateAuthorization(
        ECDsa signingKey,
        string publicKey,
        string subject,
        Uri endpoint,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(signingKey);
        ArgumentNullException.ThrowIfNull(endpoint);
        var audience = endpoint.GetLeftPart(UriPartial.Authority);
        var claims = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
        {
            ["aud"] = audience,
            ["exp"] = now.AddHours(12).ToUnixTimeSeconds(),
            ["sub"] = subject
        });
        var unsigned = Header + "." + NhAssistantBase64Url.Encode(claims);
        var signature = signingKey.SignData(
            Encoding.ASCII.GetBytes(unsigned),
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return "vapid t=" + unsigned + "." + NhAssistantBase64Url.Encode(signature) + ", k=" + publicKey;
    }
}
