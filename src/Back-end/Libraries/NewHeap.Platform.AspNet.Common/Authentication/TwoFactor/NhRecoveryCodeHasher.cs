using System.Security.Cryptography;
using System.Text;

namespace NewHeap.Platform.AspNet.Common.Authentication.TwoFactor;

/// <summary>
/// Hashes two-factor recovery codes before they reach the Identity token store.
/// </summary>
/// <remarks>
/// Identity stores recovery codes as one <c>;</c>-separated token value. A stored entry is
/// <c>nhv2:</c> and the HMAC-SHA256, under a key from <see cref="NhTwoFactorCodeKey"/>, of the
/// SHA-256 of the normalized code, so a leaked token row is useless for sign-in and cannot be tested
/// against guessed codes without the key. Entries from earlier versions are an unkeyed
/// <c>nhv1:</c> SHA-256 or the plaintext code; <see cref="Rehash"/> turns both into keyed entries.
/// </remarks>
internal static class NhRecoveryCodeHasher
{
    private const string UnkeyedPrefix = "nhv1:";
    private const string KeyedPrefix = "nhv2:";
    private const int DigestBytes = 32;

    internal static string Normalize(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(code.Length);
        foreach (var character in code)
        {
            if (!char.IsWhiteSpace(character))
            {
                builder.Append(char.ToUpperInvariant(character));
            }
        }

        return builder.ToString();
    }

    /// <summary>The entry stored for <paramref name="normalizedCode"/> under <paramref name="key"/>.</summary>
    internal static string Hash(byte[] key, string normalizedCode)
    {
        return Keyed(key, SHA256.HashData(Encoding.UTF8.GetBytes(normalizedCode)));
    }

    /// <summary>The unkeyed entry that versions before keyed hashes stored for <paramref name="normalizedCode"/>.</summary>
    internal static string UnkeyedHash(string normalizedCode)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalizedCode));
        return UnkeyedPrefix + Convert.ToBase64String(hash);
    }

    /// <summary>
    /// Returns the keyed entry under <paramref name="key"/> for an unkeyed hash or a plaintext code
    /// that an earlier version stored, or <see langword="null"/> for an entry that is keyed already or
    /// unknown. The unkeyed hash is the SHA-256 that a keyed entry is computed from, so neither form
    /// needs the code itself.
    /// </summary>
    internal static string? Rehash(byte[] key, string storedEntry)
    {
        if (storedEntry.StartsWith(KeyedPrefix, StringComparison.Ordinal))
        {
            return null;
        }

        if (storedEntry.StartsWith(UnkeyedPrefix, StringComparison.Ordinal))
        {
            Span<byte> digest = stackalloc byte[DigestBytes];
            if (!Convert.TryFromBase64String(storedEntry[UnkeyedPrefix.Length..], digest, out var written)
                || written != DigestBytes)
            {
                return null;
            }

            return Keyed(key, digest);
        }

        if (!MayBeLegacyCode(storedEntry))
        {
            return null;
        }

        var normalizedCode = Normalize(storedEntry);
        return normalizedCode.Length == 0 ? null : Hash(key, normalizedCode);
    }

    /// <summary>
    /// Whether <paramref name="trimmedCode"/> may be a plaintext code stored before NewHeap
    /// hashed recovery codes. A stored hash starts with a version prefix that ends in <c>:</c>,
    /// and Identity never generates a code with <c>:</c>, so input with one is never compared
    /// with the stored entries as it is: a copy of a stored hash does not redeem a code.
    /// </summary>
    internal static bool MayBeLegacyCode(string trimmedCode)
    {
        return trimmedCode.Length > 0 && !trimmedCode.Contains(':', StringComparison.Ordinal);
    }

    private static string Keyed(byte[] key, ReadOnlySpan<byte> digest)
    {
        return KeyedPrefix + Convert.ToBase64String(HMACSHA256.HashData(key, digest));
    }
}
