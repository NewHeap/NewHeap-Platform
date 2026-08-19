// ReSharper disable once CheckNamespace

using NewHeap.Media.FileStructureStorage.SqlServer;

namespace NewHeap.Media;

public static class NhMediaContextExtensions
{
    public static void UseSqlServerFileStructureStorage(
        this NhMediaServiceConfigurationContext serviceConfigurationContext,
        string connectionString,
        Action<FileStructureDbContextOptions>? configureDbSet = null
    )
    {
        serviceConfigurationContext.Services.AddMediaSqlServerStorage(connectionString, configureDbSet);
    }
}