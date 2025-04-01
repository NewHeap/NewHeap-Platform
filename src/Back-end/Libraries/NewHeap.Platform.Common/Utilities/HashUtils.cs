using System.Security.Cryptography;
using System.Text;

namespace NewHeap.Platform.Common.Utilities;
public static class HashUtils
{
    #region MD5

    /// <summary>
    /// Generate MD5 hash for a string
    /// </summary>
    /// <param name="input"></param>
    /// <returns></returns>
    public static string GetMD5Hash(string input)
    {
        byte[] hashBytes;
        using (var md5 = MD5.Create())
        {
            var inputBytes = Encoding.UTF8.GetBytes(input);
            hashBytes = md5.ComputeHash(inputBytes);
        }

        return GetStringFromMD5HashBytes(hashBytes);
    }

    /// <summary>
    /// Generates MD5 hash from a stream
    /// </summary>
    /// <param name="stream"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public static async Task<string> GetMD5Hash(Stream stream, CancellationToken cancellationToken = default)
    {
        if (stream.Position != 0)
            stream.Seek(0, SeekOrigin.Begin);

        byte[] hashBytes;
        using (var md5Instance = MD5.Create())
        {
            hashBytes = await md5Instance.ComputeHashAsync(stream, cancellationToken);
        }

        return GetStringFromMD5HashBytes(hashBytes);
    }

    private static string GetStringFromMD5HashBytes(byte[] hashBytes)
    {
        // Convert the byte array to hexadecimal string
        var sb = new StringBuilder();
        foreach (var t in hashBytes)
        {
            sb.Append(t.ToString("X2"));
        }

        return sb.ToString();
    }

    #endregion

    /// <summary>
    /// .GetHashCode() in net core now uses a random seed, meaning the hash is not unique per run.
    /// The hash below is deterministic between runs.
    /// This is based on the .GetHashCode() impl from .net core
    /// Taken from <see href="https://andrewlock.net/why-is-string-gethashcode-different-each-time-i-run-my-program-in-net-core/"/>
    /// </summary>
    /// <param name="str"></param>
    /// <returns></returns>
    public static int GetDeterministicHashCode(string str)
    {
        unchecked
        {
            var hash1 = (5381 << 16) + 5381;
            var hash2 = hash1;

            for (var i = 0; i < str.Length; i += 2)
            {
                hash1 = ((hash1 << 5) + hash1) ^ str[i];
                if (i == str.Length - 1)
                    break;
                hash2 = ((hash2 << 5) + hash2) ^ str[i + 1];
            }

            return hash1 + (hash2 * 1566083941);
        }
    }
}
