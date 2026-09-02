using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SampleProjectManagement.DAL.Migrations
{
    /// <inheritdoc />
    public partial class HardenBackgroundOperationDivisionScope : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_BackgroundOperations_Divisions_DivisionId",
                table: "BackgroundOperations");

            migrationBuilder.AddForeignKey(
                name: "FK_BackgroundOperations_Divisions_DivisionId",
                table: "BackgroundOperations",
                column: "DivisionId",
                principalTable: "Divisions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_BackgroundOperations_Divisions_DivisionId",
                table: "BackgroundOperations");

            migrationBuilder.AddForeignKey(
                name: "FK_BackgroundOperations_Divisions_DivisionId",
                table: "BackgroundOperations",
                column: "DivisionId",
                principalTable: "Divisions",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }
    }
}
