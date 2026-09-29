using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NewHeap.Platform.AspNet.Common.Tests;

/// <summary>
/// A WebAuthn authenticator in memory: it creates an ES256 passkey with a "none" attestation
/// and signs assertions, so passkey ceremonies can be tested without a browser.
/// </summary>
internal sealed class SoftwarePasskeyAuthenticator : IDisposable
{
    private const byte UserPresent = 0x01;
    private const byte UserVerified = 0x04;
    private const byte AttestedCredentialData = 0x40;

    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly string _rpId;
    private uint _signCount;
    private byte[]? _userHandle;

    internal SoftwarePasskeyAuthenticator(string rpId, string origin)
    {
        _rpId = rpId;
        Origin = origin;
    }

    internal byte[] CredentialId { get; } = RandomNumberGenerator.GetBytes(16);

    internal string CredentialIdText => Base64Url.EncodeToString(CredentialId);

    internal string Origin { get; }

    /// <summary>Creates the <c>PublicKeyCredential</c> JSON for WebAuthn creation options.</summary>
    internal string CreateCredential(JsonElement creationOptions, string? origin = null)
    {
        _userHandle = Base64Url.DecodeFromChars(creationOptions.GetProperty("user").GetProperty("id").GetString());

        var clientData = ClientData("webauthn.create", creationOptions.GetProperty("challenge").GetString()!, origin);
        var authenticatorData = AuthenticatorData(UserPresent | UserVerified | AttestedCredentialData, AttestedCredential());
        var attestationObject = Cbor.Map(
            (Cbor.Text("fmt"), Cbor.Text("none")),
            (Cbor.Text("attStmt"), Cbor.Map()),
            (Cbor.Text("authData"), Cbor.Bytes(authenticatorData)));

        return new JsonObject
        {
            ["id"] = CredentialIdText,
            ["rawId"] = CredentialIdText,
            ["type"] = "public-key",
            ["authenticatorAttachment"] = "platform",
            ["clientExtensionResults"] = new JsonObject(),
            ["response"] = new JsonObject
            {
                ["clientDataJSON"] = Base64Url.EncodeToString(clientData),
                ["attestationObject"] = Base64Url.EncodeToString(attestationObject),
                ["transports"] = new JsonArray("internal"),
            },
        }.ToJsonString();
    }

    /// <summary>Creates the <c>PublicKeyCredential</c> JSON for WebAuthn request options.</summary>
    internal string CreateAssertion(JsonElement requestOptions, string? origin = null)
    {
        if (_userHandle == null)
        {
            throw new InvalidOperationException("Create the credential first.");
        }

        _signCount++;

        var clientData = ClientData("webauthn.get", requestOptions.GetProperty("challenge").GetString()!, origin);
        var authenticatorData = AuthenticatorData(UserPresent | UserVerified, []);
        var signature = _key.SignData(
            [.. authenticatorData, .. SHA256.HashData(clientData)],
            HashAlgorithmName.SHA256,
            DSASignatureFormat.Rfc3279DerSequence);

        return new JsonObject
        {
            ["id"] = CredentialIdText,
            ["rawId"] = CredentialIdText,
            ["type"] = "public-key",
            ["authenticatorAttachment"] = "platform",
            ["clientExtensionResults"] = new JsonObject(),
            ["response"] = new JsonObject
            {
                ["clientDataJSON"] = Base64Url.EncodeToString(clientData),
                ["authenticatorData"] = Base64Url.EncodeToString(authenticatorData),
                ["signature"] = Base64Url.EncodeToString(signature),
                ["userHandle"] = Base64Url.EncodeToString(_userHandle),
            },
        }.ToJsonString();
    }

    public void Dispose()
    {
        _key.Dispose();
    }

    private byte[] ClientData(string type, string challenge, string? origin)
    {
        return Encoding.UTF8.GetBytes(new JsonObject
        {
            ["type"] = type,
            ["challenge"] = challenge,
            ["origin"] = origin ?? Origin,
            ["crossOrigin"] = false,
        }.ToJsonString());
    }

    private byte[] AuthenticatorData(int flags, byte[] attestedCredential)
    {
        var signCount = new byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(signCount, _signCount);

        return [
            .. SHA256.HashData(Encoding.UTF8.GetBytes(_rpId)),
            (byte)flags,
            .. signCount,
            .. attestedCredential,
        ];
    }

    private byte[] AttestedCredential()
    {
        var parameters = _key.ExportParameters(includePrivateParameters: false);
        var publicKey = Cbor.Map(
            (Cbor.Integer(1), Cbor.Integer(2)),
            (Cbor.Integer(3), Cbor.Integer(-7)),
            (Cbor.Integer(-1), Cbor.Integer(1)),
            (Cbor.Integer(-2), Cbor.Bytes(parameters.Q.X!)),
            (Cbor.Integer(-3), Cbor.Bytes(parameters.Q.Y!)));

        return [
            .. new byte[16],
            (byte)(CredentialId.Length >> 8),
            (byte)CredentialId.Length,
            .. CredentialId,
            .. publicKey,
        ];
    }

    /// <summary>
    /// The CBOR subset WebAuthn attestation needs: small integers, byte and text strings and
    /// maps, written in canonical order by the caller.
    /// </summary>
    private static class Cbor
    {
        internal static byte[] Integer(int value)
        {
            return value >= 0 ? Head(0, (ulong)value) : Head(1, (ulong)(-1 - value));
        }

        internal static byte[] Bytes(byte[] value)
        {
            return [.. Head(2, (ulong)value.Length), .. value];
        }

        internal static byte[] Text(string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            return [.. Head(3, (ulong)bytes.Length), .. bytes];
        }

        internal static byte[] Map(params (byte[] Key, byte[] Value)[] entries)
        {
            var result = new List<byte>(Head(5, (ulong)entries.Length));
            foreach (var (key, value) in entries)
            {
                result.AddRange(key);
                result.AddRange(value);
            }

            return [.. result];
        }

        private static byte[] Head(int majorType, ulong length)
        {
            var type = (byte)(majorType << 5);
            return length switch
            {
                < 24 => [(byte)(type | (byte)length)],
                <= byte.MaxValue => [(byte)(type | 24), (byte)length],
                <= ushort.MaxValue => [(byte)(type | 25), (byte)(length >> 8), (byte)length],
                _ => throw new ArgumentOutOfRangeException(nameof(length)),
            };
        }
    }
}
