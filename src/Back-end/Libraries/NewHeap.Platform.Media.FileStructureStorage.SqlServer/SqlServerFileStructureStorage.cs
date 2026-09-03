using Microsoft.EntityFrameworkCore;
using NewHeap.Media.FileStructureStorage;
using NewHeap.Media.FileStructureStorage.SqlServer.Entities;

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

    protected override IQueryable<FileEntity> WhereFilesInPath(IQueryable<FileEntity> query, string? path)
    {
        return base.WhereFilesInPath(
            query.Where(x => EF.Property<string>(x, SqlServerFileStructureModelConfiguration.PathLookupColumn)
                             == CreateLookupValue(path)),
            path);
    }

    protected override IQueryable<FolderEntity> WhereFoldersInPath(IQueryable<FolderEntity> query, string? path)
    {
        return base.WhereFoldersInPath(
            query.Where(x => EF.Property<string>(x, SqlServerFileStructureModelConfiguration.PathLookupColumn)
                             == CreateLookupValue(path)),
            path);
    }

    protected override IQueryable<FileEntity> WhereFilePathAndName(IQueryable<FileEntity> query, string? path,
        string? name)
    {
        return base.WhereFilePathAndName(
            query.Where(x => EF.Property<string>(x, SqlServerFileStructureModelConfiguration.PathNameLookupColumn)
                             == CreateLookupValue(path, name)),
            path,
            name);
    }

    protected override IQueryable<FolderEntity> WhereFolderPathAndName(IQueryable<FolderEntity> query, string? path,
        string? name)
    {
        return base.WhereFolderPathAndName(
            query.Where(x => EF.Property<string>(x, SqlServerFileStructureModelConfiguration.PathNameLookupColumn)
                             == CreateLookupValue(path, name)),
            path,
            name);
    }

    private static string CreateLookupValue(params string?[] values)
    {
        var value = string.Join("\u001F", values.Select(x => x?.ToLowerInvariant() ?? string.Empty));
        return value[..Math.Min(value.Length, 256)];
    }
}
