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
}
