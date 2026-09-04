using System.Security.Cryptography;
using System.Text;

namespace NewHeap.Media.FileStructureStorage.PostgreSql;

internal static class PostgreSqlLookupHash
{
    internal static byte[] Compute(params string?[] values)
    {
        // ponytail: MD5 keeps the migration dependency-free; Path and Name remain collision checks. Use pgcrypto SHA-256 if collision resistance becomes required.
        var normalized = string.Join("\u001F", values.Select(value => value ?? string.Empty));
        return MD5.HashData(Encoding.UTF8.GetBytes(normalized));
    }
}
