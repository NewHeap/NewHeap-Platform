using Microsoft.EntityFrameworkCore.Migrations;

namespace NewHeap.Media.FileStructureStorage.SqlServer.Migrations;

public abstract class BaseMigration : Migration
{
    public static string DefaultScheme { get; internal set; } = null!;
}