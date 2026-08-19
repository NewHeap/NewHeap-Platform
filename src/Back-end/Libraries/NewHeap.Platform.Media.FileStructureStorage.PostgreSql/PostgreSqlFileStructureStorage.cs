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

    protected override byte[] ComputeLookupHash(params string?[] values)
    {
        return HashHelper.ComputePostgreSqlHash(values);
    }
}
