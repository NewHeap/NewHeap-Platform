using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NewHeap.Media.FileStructureStorage.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class AddedProperties : BaseMigration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Creator",
                schema: DefaultScheme,
                table: "Files",
                type: "nvarchar(150)",
                maxLength: 150,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MetaData",
                schema: DefaultScheme,
                table: "Files",
                type: "NVARCHAR(MAX)",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Creator",
                schema: DefaultScheme,
                table: "Files");

            migrationBuilder.DropColumn(
                name: "MetaData",
                schema: DefaultScheme,
                table: "Files");
        }
    }
}
