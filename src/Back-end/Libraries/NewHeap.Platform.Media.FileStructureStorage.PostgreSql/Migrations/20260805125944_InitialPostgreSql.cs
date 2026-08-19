using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NewHeap.Media.FileStructureStorage.PostgreSql.Migrations
{
    /// <inheritdoc />
    public partial class InitialPostgreSql : BasePostgreSqlMigration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: DefaultScheme);

            migrationBuilder.CreateTable(
                name: "Files",
                schema: DefaultScheme,
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Path = table.Column<string>(type: "character varying(10000)", maxLength: 10000, nullable: true),
                    AltText = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Title = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    Creator = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: true),
                    Name = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    CreationDateTime = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Tags = table.Column<List<string>>(type: "text[]", nullable: false),
                    MetaData = table.Column<string>(type: "text", nullable: true),
                    PathLookupHash = table.Column<byte[]>(type: "bytea", maxLength: 32, nullable: false),
                    PathNameLookupHash = table.Column<byte[]>(type: "bytea", maxLength: 32, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Files", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Folders",
                schema: DefaultScheme,
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Path = table.Column<string>(type: "character varying(10000)", maxLength: 10000, nullable: true),
                    Name = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    PathLookupHash = table.Column<byte[]>(type: "bytea", maxLength: 32, nullable: false),
                    PathNameLookupHash = table.Column<byte[]>(type: "bytea", maxLength: 32, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Folders", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Localizations",
                schema: DefaultScheme,
                columns: table => new
                {
                    TypeName = table.Column<string>(type: "text", nullable: false),
                    EntityId = table.Column<Guid>(type: "uuid", nullable: false),
                    Language = table.Column<string>(type: "character varying(5)", maxLength: 5, nullable: false),
                    PropertyName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Value = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Localizations", x => new { x.TypeName, x.EntityId, x.Language, x.PropertyName });
                });

            migrationBuilder.CreateIndex(
                name: "IX_Files_PathLookupHash",
                schema: DefaultScheme,
                table: "Files",
                column: "PathLookupHash");

            migrationBuilder.CreateIndex(
                name: "IX_Files_PathNameLookupHash",
                schema: DefaultScheme,
                table: "Files",
                column: "PathNameLookupHash");

            migrationBuilder.CreateIndex(
                name: "IX_Folders_PathLookupHash",
                schema: DefaultScheme,
                table: "Folders",
                column: "PathLookupHash");

            migrationBuilder.CreateIndex(
                name: "IX_Folders_PathNameLookupHash",
                schema: DefaultScheme,
                table: "Folders",
                column: "PathNameLookupHash");

            migrationBuilder.CreateIndex(
                name: "IX_Localizations_TypeName_EntityId_Language",
                schema: DefaultScheme,
                table: "Localizations",
                columns: new[] { "TypeName", "EntityId", "Language" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Files",
                schema: DefaultScheme);

            migrationBuilder.DropTable(
                name: "Folders",
                schema: DefaultScheme);

            migrationBuilder.DropTable(
                name: "Localizations",
                schema: DefaultScheme);
        }
    }
}
