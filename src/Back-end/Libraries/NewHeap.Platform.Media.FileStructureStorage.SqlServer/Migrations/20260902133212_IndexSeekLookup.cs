using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NewHeap.Media.FileStructureStorage.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class IndexSeekLookup : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Folders_PathLookupHash",
                schema: "nhmedia",
                table: "Folders");

            migrationBuilder.DropIndex(
                name: "IX_Folders_PathNameLookupHash",
                schema: "nhmedia",
                table: "Folders");

            migrationBuilder.DropIndex(
                name: "IX_Files_PathLookupHash",
                schema: "nhmedia",
                table: "Files");

            migrationBuilder.DropIndex(
                name: "IX_Files_PathNameLookupHash",
                schema: "nhmedia",
                table: "Files");

            migrationBuilder.DropColumn(
                name: "PathLookupHash",
                schema: "nhmedia",
                table: "Folders");

            migrationBuilder.DropColumn(
                name: "PathNameLookupHash",
                schema: "nhmedia",
                table: "Folders");

            migrationBuilder.DropColumn(
                name: "PathLookupHash",
                schema: "nhmedia",
                table: "Files");

            migrationBuilder.DropColumn(
                name: "PathNameLookupHash",
                schema: "nhmedia",
                table: "Files");

            migrationBuilder.AddColumn<string>(
                name: "PathLookup",
                schema: "nhmedia",
                table: "Folders",
                type: "NVARCHAR(256)",
                nullable: true,
                computedColumnSql: "CONVERT(nvarchar(256), LOWER(COALESCE([Path], N'')))",
                stored: true);

            migrationBuilder.AddColumn<string>(
                name: "PathNameLookup",
                schema: "nhmedia",
                table: "Folders",
                type: "NVARCHAR(256)",
                nullable: true,
                computedColumnSql: "CONVERT(nvarchar(256), LOWER(COALESCE([Path], N'')) + NCHAR(31) + LOWER([Name]))",
                stored: true);

            migrationBuilder.AddColumn<string>(
                name: "PathLookup",
                schema: "nhmedia",
                table: "Files",
                type: "NVARCHAR(256)",
                nullable: true,
                computedColumnSql: "CONVERT(nvarchar(256), LOWER(COALESCE([Path], N'')))",
                stored: true);

            migrationBuilder.AddColumn<string>(
                name: "PathNameLookup",
                schema: "nhmedia",
                table: "Files",
                type: "NVARCHAR(256)",
                nullable: true,
                computedColumnSql: "CONVERT(nvarchar(256), LOWER(COALESCE([Path], N'')) + NCHAR(31) + LOWER([Name]))",
                stored: true);

            migrationBuilder.CreateIndex(
                name: "IX_Folders_PathLookup",
                schema: "nhmedia",
                table: "Folders",
                column: "PathLookup")
                .Annotation("SqlServer:Include", new[] { "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_Folders_PathNameLookup",
                schema: "nhmedia",
                table: "Folders",
                column: "PathNameLookup")
                .Annotation("SqlServer:Include", new[] { "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_Files_PathLookup",
                schema: "nhmedia",
                table: "Files",
                column: "PathLookup")
                .Annotation("SqlServer:Include", new[] { "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_Files_PathNameLookup",
                schema: "nhmedia",
                table: "Files",
                column: "PathNameLookup")
                .Annotation("SqlServer:Include", new[] { "Id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Folders_PathLookup",
                schema: "nhmedia",
                table: "Folders");

            migrationBuilder.DropIndex(
                name: "IX_Folders_PathNameLookup",
                schema: "nhmedia",
                table: "Folders");

            migrationBuilder.DropIndex(
                name: "IX_Files_PathLookup",
                schema: "nhmedia",
                table: "Files");

            migrationBuilder.DropIndex(
                name: "IX_Files_PathNameLookup",
                schema: "nhmedia",
                table: "Files");

            migrationBuilder.DropColumn(
                name: "PathLookup",
                schema: "nhmedia",
                table: "Folders");

            migrationBuilder.DropColumn(
                name: "PathNameLookup",
                schema: "nhmedia",
                table: "Folders");

            migrationBuilder.DropColumn(
                name: "PathLookup",
                schema: "nhmedia",
                table: "Files");

            migrationBuilder.DropColumn(
                name: "PathNameLookup",
                schema: "nhmedia",
                table: "Files");

            migrationBuilder.AddColumn<byte[]>(
                name: "PathLookupHash",
                schema: "nhmedia",
                table: "Folders",
                type: "binary(32)",
                nullable: true,
                computedColumnSql: "CONVERT(binary(32), HASHBYTES('SHA2_256', LOWER(COALESCE([Path], N''))))",
                stored: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "PathNameLookupHash",
                schema: "nhmedia",
                table: "Folders",
                type: "binary(32)",
                nullable: true,
                computedColumnSql: "CONVERT(binary(32), HASHBYTES('SHA2_256', CONCAT(LOWER(COALESCE([Path], N'')), NCHAR(31), LOWER(COALESCE([Name], N'')))))",
                stored: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "PathLookupHash",
                schema: "nhmedia",
                table: "Files",
                type: "binary(32)",
                nullable: true,
                computedColumnSql: "CONVERT(binary(32), HASHBYTES('SHA2_256', LOWER(COALESCE([Path], N''))))",
                stored: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "PathNameLookupHash",
                schema: "nhmedia",
                table: "Files",
                type: "binary(32)",
                nullable: true,
                computedColumnSql: "CONVERT(binary(32), HASHBYTES('SHA2_256', CONCAT(LOWER(COALESCE([Path], N'')), NCHAR(31), LOWER(COALESCE([Name], N'')))))",
                stored: true);

            migrationBuilder.CreateIndex(
                name: "IX_Folders_PathLookupHash",
                schema: "nhmedia",
                table: "Folders",
                column: "PathLookupHash")
                .Annotation("SqlServer:Include", new[] { "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_Folders_PathNameLookupHash",
                schema: "nhmedia",
                table: "Folders",
                column: "PathNameLookupHash")
                .Annotation("SqlServer:Include", new[] { "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_Files_PathLookupHash",
                schema: "nhmedia",
                table: "Files",
                column: "PathLookupHash")
                .Annotation("SqlServer:Include", new[] { "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_Files_PathNameLookupHash",
                schema: "nhmedia",
                table: "Files",
                column: "PathNameLookupHash")
                .Annotation("SqlServer:Include", new[] { "Id" });
        }
    }
}
