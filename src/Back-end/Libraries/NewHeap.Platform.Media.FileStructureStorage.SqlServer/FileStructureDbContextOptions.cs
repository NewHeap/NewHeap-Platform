namespace NewHeap.Media.FileStructureStorage.SqlServer;

public class FileStructureDbContextOptions
{
    /// <summary>
    /// Database schema to use. Defaults to 'nhmedia'
    /// </summary>
    public string Scheme { get; set; } = "nhmedia";

    /// <summary>
    /// Execute DbSet migrations on startup. Defaults to true
    /// </summary>
    public bool RunMigrations { get; set; } = true;
}