using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace NewHeap.Media.FileStructureStorage;

/// <summary>
/// Keys the relational media model by its configured schema, so registrations with different schemas never share a model.
/// </summary>
internal sealed class FileStructureModelCacheKeyFactory : IModelCacheKeyFactory
{
    internal const string DefaultSchema = "nhmedia";

    public object Create(DbContext context, bool designTime)
    {
        return (context.GetType(), GetSchema(context), designTime);
    }

    // The providers keep the migration history in the configured media schema.
    internal static string GetSchema(DbContext context)
    {
        return RelationalOptionsExtension.Extract(context.GetService<IDbContextOptions>())
            .MigrationsHistoryTableSchema ?? DefaultSchema;
    }
}
