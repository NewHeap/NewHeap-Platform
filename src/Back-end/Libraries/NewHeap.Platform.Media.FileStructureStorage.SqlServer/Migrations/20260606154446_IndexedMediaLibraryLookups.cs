using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NewHeap.Media.FileStructureStorage.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class IndexedMediaLibraryLookups : BaseMigration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<byte[]>(
                name: "PathLookupHash",
                schema: DefaultScheme,
                table: "Folders",
                type: "binary(32)",
                nullable: true,
                computedColumnSql: "CONVERT(binary(32), HASHBYTES('SHA2_256', LOWER(COALESCE([Path], N''))))",
                stored: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "PathNameLookupHash",
                schema: DefaultScheme,
                table: "Folders",
                type: "binary(32)",
                nullable: true,
                computedColumnSql: "CONVERT(binary(32), HASHBYTES('SHA2_256', CONCAT(LOWER(COALESCE([Path], N'')), NCHAR(31), LOWER(COALESCE([Name], N'')))))",
                stored: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "PathLookupHash",
                schema: DefaultScheme,
                table: "Files",
                type: "binary(32)",
                nullable: true,
                computedColumnSql: "CONVERT(binary(32), HASHBYTES('SHA2_256', LOWER(COALESCE([Path], N''))))",
                stored: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "PathNameLookupHash",
                schema: DefaultScheme,
                table: "Files",
                type: "binary(32)",
                nullable: true,
                computedColumnSql: "CONVERT(binary(32), HASHBYTES('SHA2_256', CONCAT(LOWER(COALESCE([Path], N'')), NCHAR(31), LOWER(COALESCE([Name], N'')))))",
                stored: true);

            migrationBuilder.CreateIndex(
                name: "IX_Folders_PathLookupHash",
                schema: DefaultScheme,
                table: "Folders",
                column: "PathLookupHash")
                .Annotation("SqlServer:Include", new[] { "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_Folders_PathNameLookupHash",
                schema: DefaultScheme,
                table: "Folders",
                column: "PathNameLookupHash")
                .Annotation("SqlServer:Include", new[] { "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_Files_PathLookupHash",
                schema: DefaultScheme,
                table: "Files",
                column: "PathLookupHash")
                .Annotation("SqlServer:Include", new[] { "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_Files_PathNameLookupHash",
                schema: DefaultScheme,
                table: "Files",
                column: "PathNameLookupHash")
                .Annotation("SqlServer:Include", new[] { "Id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Folders_PathLookupHash",
                schema: DefaultScheme,
                table: "Folders");

            migrationBuilder.DropIndex(
                name: "IX_Folders_PathNameLookupHash",
                schema: DefaultScheme,
                table: "Folders");

            migrationBuilder.DropIndex(
                name: "IX_Files_PathLookupHash",
                schema: DefaultScheme,
                table: "Files");

            migrationBuilder.DropIndex(
                name: "IX_Files_PathNameLookupHash",
                schema: DefaultScheme,
                table: "Files");

            migrationBuilder.DropColumn(
                name: "PathLookupHash",
                schema: DefaultScheme,
                table: "Folders");

            migrationBuilder.DropColumn(
                name: "PathNameLookupHash",
                schema: DefaultScheme,
                table: "Folders");

            migrationBuilder.DropColumn(
                name: "PathLookupHash",
                schema: DefaultScheme,
                table: "Files");

            migrationBuilder.DropColumn(
                name: "PathNameLookupHash",
                schema: DefaultScheme,
                table: "Files");
        }
    }
}
