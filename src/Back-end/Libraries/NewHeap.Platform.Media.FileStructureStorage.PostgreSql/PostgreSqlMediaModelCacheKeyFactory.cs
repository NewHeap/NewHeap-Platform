using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace NewHeap.Media.FileStructureStorage.PostgreSql;

internal sealed class PostgreSqlMediaModelCacheKeyFactory : IModelCacheKeyFactory
{
    public object Create(DbContext context, bool designTime)
    {
        return (context.GetType(), GetSchema(context), designTime);
    }

    internal static string GetSchema(DbContext context)
    {
        return RelationalOptionsExtension.Extract(context.GetService<IDbContextOptions>())
            .MigrationsHistoryTableSchema ?? "nhmedia";
    }
}
