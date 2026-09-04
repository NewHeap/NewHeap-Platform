using Microsoft.EntityFrameworkCore;
using NewHeap.Media.FileStructureStorage;
using NewHeap.Media.FileStructureStorage.SqlServer;
using NewHeap.Media.FileStructureStorage.SqlServer.Entities;

namespace NewHeap.Media.FileStructureStorage.PostgreSql;

/// <summary>
/// PostgreSQL implementation of the file-structure storage.
/// </summary>
public sealed class PostgreSqlFileStructureStorage : RelationalFileStructureStorage
{
    public PostgreSqlFileStructureStorage(FileStructureDbContext dbContext)
        : base(dbContext)
    {
    }

    protected override IQueryable<FileEntity> WhereFilesInPath(IQueryable<FileEntity> query, string? path)
    {
        return base.WhereFilesInPath(
            query.Where(x => EF.Property<byte[]>(x, PostgreSqlFileStructureModelConfiguration.PathLookupHashColumn)
                             == PostgreSqlLookupHash.Compute(path)),
            path);
    }

    protected override IQueryable<FolderEntity> WhereFoldersInPath(IQueryable<FolderEntity> query, string? path)
    {
        return base.WhereFoldersInPath(
            query.Where(x => EF.Property<byte[]>(x, PostgreSqlFileStructureModelConfiguration.PathLookupHashColumn)
                             == PostgreSqlLookupHash.Compute(path)),
            path);
    }

    protected override IQueryable<FileEntity> WhereFilePathAndName(IQueryable<FileEntity> query, string? path,
        string? name)
    {
        return base.WhereFilePathAndName(
            query.Where(x => EF.Property<byte[]>(x, PostgreSqlFileStructureModelConfiguration.PathNameLookupHashColumn)
                             == PostgreSqlLookupHash.Compute(path, name)),
            path,
            name);
    }

    protected override IQueryable<FolderEntity> WhereFolderPathAndName(IQueryable<FolderEntity> query, string? path,
        string? name)
    {
        return base.WhereFolderPathAndName(
            query.Where(x => EF.Property<byte[]>(x, PostgreSqlFileStructureModelConfiguration.PathNameLookupHashColumn)
                             == PostgreSqlLookupHash.Compute(path, name)),
            path,
            name);
    }
}
