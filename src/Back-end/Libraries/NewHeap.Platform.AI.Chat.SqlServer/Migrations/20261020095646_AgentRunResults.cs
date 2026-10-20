using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NewHeap.Platform.AI.Chat.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class AgentRunResults : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Result",
                schema: "nhai",
                table: "AssistantRun",
                type: "nvarchar(max)",
                maxLength: 20000,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "ResultTruncated",
                schema: "nhai",
                table: "AssistantRun",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "IX_AssistantThread_AgentId_SubjectType",
                schema: "nhai",
                table: "AssistantThread",
                columns: new[] { "AgentId", "SubjectType" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AssistantThread_AgentId_SubjectType",
                schema: "nhai",
                table: "AssistantThread");

            migrationBuilder.DropColumn(
                name: "Result",
                schema: "nhai",
                table: "AssistantRun");

            migrationBuilder.DropColumn(
                name: "ResultTruncated",
                schema: "nhai",
                table: "AssistantRun");
        }
    }
}
