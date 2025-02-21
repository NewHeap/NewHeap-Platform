// ReSharper disable once CheckNamespace

using NewHeap.Media.FileStructureStorage.SqlServer;

namespace NewHeap.Media;

public static class NhMediaContextExtensions
{
    public static void UseSqlServerFileStructureStorage(
        this NhMediaContext context,
        string connectionString,
        Action<FileStructureDbContextOptions>? configureDbSet = null
    )
    {
        context.Services.AddMediaSqlServerStorage(connectionString, configureDbSet);
    }
}