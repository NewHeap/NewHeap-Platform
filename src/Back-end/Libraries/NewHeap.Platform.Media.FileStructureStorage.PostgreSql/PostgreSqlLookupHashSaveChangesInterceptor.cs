using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NewHeap.Media.FileStructureStorage.SqlServer;
using NewHeap.Media.FileStructureStorage.SqlServer.Entities;

namespace NewHeap.Media.FileStructureStorage.PostgreSql;

internal sealed class PostgreSqlLookupHashSaveChangesInterceptor : SaveChangesInterceptor
{
    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        PopulateLookupHashes(eventData.Context);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
        InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        PopulateLookupHashes(eventData.Context);
        return ValueTask.FromResult(result);
    }

    private static void PopulateLookupHashes(DbContext? dbContext)
    {
        if (dbContext is not FileStructureDbContext context)
        {
            return;
        }

        foreach (var entry in context.ChangeTracker.Entries<FileEntity>()
                     .Where(entry => entry.State is EntityState.Added or EntityState.Modified))
        {
            SetLookupHashes(entry, entry.Entity.Path, entry.Entity.Name);
        }

        foreach (var entry in context.ChangeTracker.Entries<FolderEntity>()
                     .Where(entry => entry.State is EntityState.Added or EntityState.Modified))
        {
            SetLookupHashes(entry, entry.Entity.Path, entry.Entity.Name);
        }
    }

    private static void SetLookupHashes<TEntity>(Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry<TEntity> entry,
        string? path, string? name)
        where TEntity : class
    {
        entry.Property<byte[]>(PostgreSqlFileStructureModelConfiguration.PathLookupHashColumn).CurrentValue =
            PostgreSqlLookupHash.Compute(path);
        entry.Property<byte[]>(PostgreSqlFileStructureModelConfiguration.PathNameLookupHashColumn).CurrentValue =
            PostgreSqlLookupHash.Compute(path, name);
    }
}
