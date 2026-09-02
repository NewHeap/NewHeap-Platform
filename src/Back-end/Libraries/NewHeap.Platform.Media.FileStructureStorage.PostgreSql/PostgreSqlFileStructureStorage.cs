using NewHeap.Media.FileStructureStorage;
using NewHeap.Media.FileStructureStorage.SqlServer;

namespace NewHeap.Media.FileStructureStorage.PostgreSql;

/// <summary>
/// PostgreSQL implementation of the file-structure storage.
/// Lookup hashes are calculated in the application and stored in <c>bytea</c> columns.
/// </summary>
public sealed class PostgreSqlFileStructureStorage : RelationalFileStructureStorage
{
    public PostgreSqlFileStructureStorage(FileStructureDbContext dbContext)
        : base(dbContext)
    {
    }

    protected override string ComputeLookupHash(params string?[] values)
    {
        var value = string.Join("\u001F", values.Select(x => x ?? string.Empty));
        value = value[..Math.Min(value.Length, 256)];
        return value;
    }
}
