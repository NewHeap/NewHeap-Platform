using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;

namespace NewHeap.Platform.AspNet.Common.Test;

/// <summary>
/// Generates authenticator-app codes in tests of applications that use NewHeap two-factor
/// authentication. The implementation follows RFC 6238 independently of the library, so a
/// test also proves that real authenticator apps interoperate.
/// </summary>
public static class NhTwoFactorTestCodes
{
    private const string Base32Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    /// <summary>
    /// Returns the six-digit code for an authenticator key at <paramref name="moment"/>
    /// (the current UTC time by default).
    /// </summary>
    /// <param name="authenticatorKey">
    /// The base32 key, as stored by Identity or shown in the <c>sharedKey</c> of an
    /// authenticator setup response (spaces and case are ignored).
    /// </param>
    /// <param name="moment">The moment the code is valid for.</param>
    public static string AuthenticatorCode(string authenticatorKey, DateTimeOffset? moment = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(authenticatorKey);

        var timeStep = (moment ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds() / 30;

        Span<byte> counter = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64BigEndian(counter, timeStep);

        var hash = HMACSHA1.HashData(DecodeBase32(authenticatorKey), counter);
        var offset = hash[^1] & 0x0F;
        var binary = ((hash[offset] & 0x7F) << 24)
            | (hash[offset + 1] << 16)
            | (hash[offset + 2] << 8)
            | hash[offset + 3];

        return (binary % 1_000_000).ToString("D6", CultureInfo.InvariantCulture);
    }

    private static byte[] DecodeBase32(string input)
    {
        var output = new List<byte>(input.Length * 5 / 8);
        var buffer = 0;
        var bitsInBuffer = 0;

        foreach (var character in input)
        {
            if (char.IsWhiteSpace(character) || character == '=')
            {
                continue;
            }

            var value = Base32Alphabet.IndexOf(char.ToUpperInvariant(character));
            if (value < 0)
            {
                throw new ArgumentException("The authenticator key is not valid base32.", nameof(input));
            }

            buffer = (buffer << 5) | value;
            bitsInBuffer += 5;

            if (bitsInBuffer >= 8)
            {
                bitsInBuffer -= 8;
                output.Add((byte)((buffer >> bitsInBuffer) & 0xFF));
            }
        }

        return output.ToArray();
    }
}
