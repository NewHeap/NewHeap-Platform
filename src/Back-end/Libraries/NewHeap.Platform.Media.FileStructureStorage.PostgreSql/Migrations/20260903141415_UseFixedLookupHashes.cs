using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NewHeap.Media.FileStructureStorage.PostgreSql.Migrations
{
    /// <inheritdoc />
    public partial class UseFixedLookupHashes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
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
                type: "bytea",
                maxLength: 16,
                nullable: false,
                defaultValue: new byte[0]);

            migrationBuilder.AddColumn<byte[]>(
                name: "PathNameLookupHash",
                schema: "nhmedia",
                table: "Folders",
                type: "bytea",
                maxLength: 16,
                nullable: false,
                defaultValue: new byte[0]);

            migrationBuilder.AddColumn<byte[]>(
                name: "PathLookupHash",
                schema: "nhmedia",
                table: "Files",
                type: "bytea",
                maxLength: 16,
                nullable: false,
                defaultValue: new byte[0]);

            migrationBuilder.AddColumn<byte[]>(
                name: "PathNameLookupHash",
                schema: "nhmedia",
                table: "Files",
                type: "bytea",
                maxLength: 16,
                nullable: false,
                defaultValue: new byte[0]);

            migrationBuilder.Sql("""
                UPDATE "nhmedia"."Folders"
                SET "PathLookupHash" = decode(md5(coalesce("Path", '')), 'hex'),
                    "PathNameLookupHash" = decode(md5(coalesce("Path", '') || chr(31) || "Name"), 'hex');
                """);

            migrationBuilder.Sql("""
                UPDATE "nhmedia"."Files"
                SET "PathLookupHash" = decode(md5(coalesce("Path", '')), 'hex'),
                    "PathNameLookupHash" = decode(md5(coalesce("Path", '') || chr(31) || "Name"), 'hex');
                """);

            migrationBuilder.CreateIndex(
                name: "IX_Folders_PathLookupHash",
                schema: "nhmedia",
                table: "Folders",
                column: "PathLookupHash");

            migrationBuilder.CreateIndex(
                name: "IX_Folders_PathNameLookupHash",
                schema: "nhmedia",
                table: "Folders",
                column: "PathNameLookupHash");

            migrationBuilder.CreateIndex(
                name: "IX_Files_PathLookupHash",
                schema: "nhmedia",
                table: "Files",
                column: "PathLookupHash");

            migrationBuilder.CreateIndex(
                name: "IX_Files_PathNameLookupHash",
                schema: "nhmedia",
                table: "Files",
                column: "PathNameLookupHash");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
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
                type: "character varying(256)",
                maxLength: 256,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "PathNameLookup",
                schema: "nhmedia",
                table: "Folders",
                type: "character varying(256)",
                maxLength: 256,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "PathLookup",
                schema: "nhmedia",
                table: "Files",
                type: "character varying(256)",
                maxLength: 256,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "PathNameLookup",
                schema: "nhmedia",
                table: "Files",
                type: "character varying(256)",
                maxLength: 256,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateIndex(
                name: "IX_Folders_PathLookup",
                schema: "nhmedia",
                table: "Folders",
                column: "PathLookup");

            migrationBuilder.CreateIndex(
                name: "IX_Folders_PathNameLookup",
                schema: "nhmedia",
                table: "Folders",
                column: "PathNameLookup");

            migrationBuilder.CreateIndex(
                name: "IX_Files_PathLookup",
                schema: "nhmedia",
                table: "Files",
                column: "PathLookup");

            migrationBuilder.CreateIndex(
                name: "IX_Files_PathNameLookup",
                schema: "nhmedia",
                table: "Files",
                column: "PathNameLookup");
        }
    }
}
