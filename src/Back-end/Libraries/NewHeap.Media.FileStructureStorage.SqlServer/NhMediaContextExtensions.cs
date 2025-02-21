// ReSharper disable once CheckNamespace
namespace NewHeap.Media;

public static class NhMediaContextExtensions
{
    public static void UseSqlServerFileStructureStorage(this NhMediaContext context, string connectionString)
    {
        context.Services.AddMediaSqlServerStorage(connectionString);
    }
}