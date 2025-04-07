using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NewHeap.Media.FileStructureStorage.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class MediaSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "nhmedia");

            migrationBuilder.RenameTable(
                name: "Localizations",
                newName: "Localizations",
                newSchema: "nhmedia");

            migrationBuilder.RenameTable(
                name: "Folders",
                newName: "Folders",
                newSchema: "nhmedia");

            migrationBuilder.RenameTable(
                name: "Files",
                newName: "Files",
                newSchema: "nhmedia");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameTable(
                name: "Localizations",
                schema: "nhmedia",
                newName: "Localizations");

            migrationBuilder.RenameTable(
                name: "Folders",
                schema: "nhmedia",
                newName: "Folders");

            migrationBuilder.RenameTable(
                name: "Files",
                schema: "nhmedia",
                newName: "Files");
        }
    }
}
