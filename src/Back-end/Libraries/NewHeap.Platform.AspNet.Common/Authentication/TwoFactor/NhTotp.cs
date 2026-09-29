using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;

namespace NewHeap.Platform.AspNet.Common.Authentication.TwoFactor;

/// <summary>
/// RFC 6238 time-based one-time passwords with the parameters used by authenticator apps
/// and ASP.NET Core Identity keys: HMAC-SHA1, 30-second steps and six digits.
/// </summary>
/// <remarks>
/// Identity's own authenticator provider does not report which time step matched, so it
/// cannot reject a code that was already used. This implementation returns the matched
/// step, which lets the caller store it and refuse the same or an earlier step.
/// </remarks>
internal static class NhTotp
{
    internal const int Digits = 6;
    private const int StepSeconds = 30;
    private const int AllowedDriftSteps = 1;

    internal static long GetTimeStep(DateTimeOffset moment)
    {
        return moment.ToUnixTimeSeconds() / StepSeconds;
    }

    internal static string ComputeCode(byte[] key, long timeStep)
    {
        Span<byte> counter = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64BigEndian(counter, timeStep);

        Span<byte> hash = stackalloc byte[HMACSHA1.HashSizeInBytes];
        HMACSHA1.HashData(key, counter, hash);

        var offset = hash[^1] & 0x0F;
        var binary = ((hash[offset] & 0x7F) << 24)
            | (hash[offset + 1] << 16)
            | (hash[offset + 2] << 8)
            | hash[offset + 3];

        var code = binary % 1_000_000;
        return code.ToString("D6", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Returns the matched time step, or <see langword="null"/> when the code is invalid or
    /// belongs to a step that is not newer than <paramref name="lastAcceptedStep"/>.
    /// </summary>
    internal static long? Match(
        string base32Key,
        string? code,
        DateTimeOffset now,
        long? lastAcceptedStep)
    {
        var normalizedCode = NormalizeCode(code);
        if (normalizedCode == null)
        {
            return null;
        }

        var key = NhBase32.Decode(base32Key);
        if (key.Length == 0)
        {
            return null;
        }

        var presented = Encoding.ASCII.GetBytes(normalizedCode);
        var currentStep = GetTimeStep(now);

        for (var drift = -AllowedDriftSteps; drift <= AllowedDriftSteps; drift++)
        {
            var step = currentStep + drift;
            if (lastAcceptedStep.HasValue && step <= lastAcceptedStep.Value)
            {
                continue;
            }

            var expected = Encoding.ASCII.GetBytes(ComputeCode(key, step));
            if (CryptographicOperations.FixedTimeEquals(expected, presented))
            {
                return step;
            }
        }

        return null;
    }

    internal static string FormatSharedKey(string base32Key)
    {
        var builder = new StringBuilder(base32Key.Length + base32Key.Length / 4);
        for (var index = 0; index < base32Key.Length; index++)
        {
            if (index > 0 && index % 4 == 0)
            {
                builder.Append(' ');
            }

            builder.Append(char.ToLowerInvariant(base32Key[index]));
        }

        return builder.ToString();
    }

    internal static string CreateAuthenticatorUri(string issuer, string accountName, string base32Key)
    {
        var encodedIssuer = UrlEncoder.Default.Encode(issuer);
        var encodedAccount = UrlEncoder.Default.Encode(accountName);

        return $"otpauth://totp/{encodedIssuer}:{encodedAccount}?secret={base32Key}&issuer={encodedIssuer}&digits={Digits}";
    }

    private static string? NormalizeCode(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return null;
        }

        var builder = new StringBuilder(Digits);
        foreach (var character in code)
        {
            if (char.IsWhiteSpace(character) || character == '-')
            {
                continue;
            }

            if (!char.IsAsciiDigit(character))
            {
                return null;
            }

            builder.Append(character);
        }

        if (builder.Length != Digits)
        {
            return null;
        }

        return builder.ToString();
    }
}

/// <summary>
/// RFC 4648 base32 decoding for authenticator keys, which Identity stores without padding.
/// </summary>
internal static class NhBase32
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    internal static byte[] Decode(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return [];
        }

        var output = new List<byte>(input.Length * 5 / 8);
        var buffer = 0;
        var bitsInBuffer = 0;

        foreach (var character in input)
        {
            if (character == '=' || char.IsWhiteSpace(character))
            {
                continue;
            }

            var value = Alphabet.IndexOf(char.ToUpperInvariant(character));
            if (value < 0)
            {
                return [];
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
