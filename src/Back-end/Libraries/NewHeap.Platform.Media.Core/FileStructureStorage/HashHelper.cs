using System.Security.Cryptography;
using System.Text;

namespace NewHeap.Media.FileStructureStorage.SqlServer;

public static class HashHelper
{

    public static string ComputePostgreSqlHash(params string?[] values)
    {
        var value = string.Join("\u001F", values.Select(x => x?.ToLowerInvariant() ?? string.Empty));
        value = value[..Math.Min(value.Length, 256)];
        return value;
    }
}
