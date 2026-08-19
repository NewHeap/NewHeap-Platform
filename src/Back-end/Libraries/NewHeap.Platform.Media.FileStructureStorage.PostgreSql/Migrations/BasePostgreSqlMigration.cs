using Microsoft.EntityFrameworkCore.Migrations;

namespace NewHeap.Media.FileStructureStorage.PostgreSql.Migrations;

public abstract class BasePostgreSqlMigration : Migration
{
    public static string DefaultScheme { get; set; } = "nhmedia";
}
