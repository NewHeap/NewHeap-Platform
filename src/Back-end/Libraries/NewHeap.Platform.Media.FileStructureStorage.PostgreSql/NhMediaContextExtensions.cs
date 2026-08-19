using NewHeap.Media.FileStructureStorage.SqlServer;

// ReSharper disable once CheckNamespace
namespace NewHeap.Media;

public static class PostgreSqlNhMediaContextExtensions
{
    public static void UsePostgreSqlFileStructureStorage(
        this NhMediaServiceConfigurationContext serviceConfigurationContext,
        string connectionString,
        Action<FileStructureDbContextOptions>? configureDbSet = null)
    {
        serviceConfigurationContext.Services.AddMediaPostgreSqlStorage(connectionString, configureDbSet);
    }
}
