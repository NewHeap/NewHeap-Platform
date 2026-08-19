using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NewHeap.Media.FileStructureStorage.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class LocalizationsAdded : BaseMigration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AltText",
                table: "Files",
                schema: DefaultScheme,
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Description",
                table: "Files",
                schema: DefaultScheme,
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Title",
                table: "Files",
                schema: DefaultScheme,
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Localizations",
                schema: DefaultScheme,
                columns: table => new
                {
                    TypeName = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    EntityId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Language = table.Column<string>(type: "nvarchar(5)", maxLength: 5, nullable: false),
                    PropertyName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Value = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Localizations", x => new { x.TypeName, x.EntityId, x.Language, x.PropertyName });
                });

            migrationBuilder.CreateIndex(
                schema: DefaultScheme,
                name: "IX_Localizations_TypeName_EntityId_Language",
                table: "Localizations",
                columns: new[] { "TypeName", "EntityId", "Language" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                schema: DefaultScheme,
                name: "Localizations");

            migrationBuilder.DropColumn(
                schema: DefaultScheme,
                name: "AltText",
                table: "Files");

            migrationBuilder.DropColumn(
                schema: DefaultScheme,
                name: "Description",
                table: "Files");

            migrationBuilder.DropColumn(
                schema: DefaultScheme,
                name: "Title",
                table: "Files");
        }
    }
}
