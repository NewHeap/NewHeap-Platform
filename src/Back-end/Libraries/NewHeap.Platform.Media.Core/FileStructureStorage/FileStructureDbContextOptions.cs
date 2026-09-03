namespace NewHeap.Media.FileStructureStorage.SqlServer;

using Microsoft.EntityFrameworkCore;

public class FileStructureDbContextOptions
{
    internal Action<ModelBuilder>? ConfigureProviderModel { get; set; }
    internal Func<string?[], string>? LookupHashFactory { get; set; }

    /// <summary>
    /// Database schema to use. Defaults to 'nhmedia'
    /// </summary>
    public string Scheme { get; set; } = "nhmedia";

    /// <summary>
    /// Execute DbSet migrations on startup. Defaults to true
    /// </summary>
    public bool RunMigrations { get; set; } = true;
}
