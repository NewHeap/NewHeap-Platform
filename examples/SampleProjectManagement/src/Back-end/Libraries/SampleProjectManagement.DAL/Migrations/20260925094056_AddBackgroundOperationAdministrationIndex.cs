using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SampleProjectManagement.DAL.Migrations
{
    /// <inheritdoc />
    public partial class AddBackgroundOperationAdministrationIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_BackgroundOperations_DivisionId_ParentOperationId_Status_La~",
                table: "BackgroundOperations",
                columns: new[] { "DivisionId", "ParentOperationId", "Status", "LastModifiedDateTime" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_BackgroundOperations_DivisionId_ParentOperationId_Status_La~",
                table: "BackgroundOperations");
        }
    }
}
