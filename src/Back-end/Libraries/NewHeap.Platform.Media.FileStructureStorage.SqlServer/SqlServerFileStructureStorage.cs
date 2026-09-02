using NewHeap.Media.FileStructureStorage;

namespace NewHeap.Media.FileStructureStorage.SqlServer;

/// <summary>
/// SQL Server implementation of the relational media file-structure contract.
/// </summary>
public class SqlServerFileStructureStorage : RelationalFileStructureStorage
{
    public SqlServerFileStructureStorage(FileStructureDbContext dbContext)
        : base(dbContext)
    {
    }

    protected override string ComputeLookupHash(params string?[] values)
    {
        var value = string.Join("\u001F", values.Select(x => x?.ToLowerInvariant() ?? string.Empty));
        value = value[..Math.Min(value.Length, 256)];
        return value;
    }
}
