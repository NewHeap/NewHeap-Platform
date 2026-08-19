using System.Security.Cryptography;
using System.Text;

namespace NewHeap.Media.FileStructureStorage.SqlServer;

public static class HashHelper
{
    public static byte[] ComputeHash(params string?[] values)
    {
        var normalized = Normalize(values);
        return SHA256.HashData(Encoding.Unicode.GetBytes(normalized));
    }

    public static byte[] ComputePostgreSqlHash(params string?[] values)
    {
        return SHA256.HashData(Encoding.UTF8.GetBytes(Normalize(values)));
    }

    private static string Normalize(IEnumerable<string?> values)
    {
        return string.Join("\u001F", values.Select(value => value?.ToLowerInvariant() ?? string.Empty));
    }
}
