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

    protected override byte[] ComputeLookupHash(params string?[] values)
    {
        return HashHelper.ComputeHash(values);
    }
}
