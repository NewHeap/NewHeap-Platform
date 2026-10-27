using System.Security.Cryptography;
using System.Text;

namespace NewHeap.Platform.AspNet.Common.Authentication.TwoFactor;

/// <summary>
/// Hashes two-factor recovery codes before they reach the Identity token store.
/// </summary>
/// <remarks>
/// Identity stores recovery codes as one <c>;</c>-separated token value. The hash keeps
/// that format intact while making a leaked token row useless for sign-in.
/// </remarks>
internal static class NhRecoveryCodeHasher
{
    private const string VersionPrefix = "nhv1:";

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

    internal static string Hash(string normalizedCode)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalizedCode));
        return VersionPrefix + Convert.ToBase64String(hash);
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
}
