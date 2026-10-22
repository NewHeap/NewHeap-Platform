using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NewHeap.Platform.AI.Chat.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class McpServerReaders : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ReaderMaxClassification",
                schema: "nhai",
                table: "AssistantMcpServer",
                type: "nvarchar(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReaderMediaTypes",
                schema: "nhai",
                table: "AssistantMcpServer",
                type: "nvarchar(2064)",
                maxLength: 2064,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReaderToolName",
                schema: "nhai",
                table: "AssistantMcpServer",
                type: "nvarchar(128)",
                maxLength: 128,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ReaderMaxClassification",
                schema: "nhai",
                table: "AssistantMcpServer");

            migrationBuilder.DropColumn(
                name: "ReaderMediaTypes",
                schema: "nhai",
                table: "AssistantMcpServer");

            migrationBuilder.DropColumn(
                name: "ReaderToolName",
                schema: "nhai",
                table: "AssistantMcpServer");
        }
    }
}
