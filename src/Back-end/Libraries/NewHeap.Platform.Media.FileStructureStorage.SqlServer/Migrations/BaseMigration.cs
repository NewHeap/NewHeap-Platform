using Microsoft.EntityFrameworkCore.Migrations;

namespace NewHeap.Media.FileStructureStorage.SqlServer.Migrations;

public abstract class BaseMigration : Migration
{
    /// <summary>
    /// Schema in which the SQL Server media migrations are authored. When the provider generates SQL, it routes
    /// every migration operation to the configured <see cref="FileStructureDbContextOptions.Scheme"/>.
    /// </summary>
    public static string DefaultScheme { get; } = "nhmedia";
}
