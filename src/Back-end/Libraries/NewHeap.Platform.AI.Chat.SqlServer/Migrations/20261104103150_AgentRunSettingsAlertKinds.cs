using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NewHeap.Platform.AI.Chat.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class AgentRunSettingsAlertKinds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AlertKindsJson",
                schema: "nhai",
                table: "AssistantAgentRunConfiguration",
                type: "nvarchar(1342)",
                maxLength: 1342,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AlertKindsJson",
                schema: "nhai",
                table: "AssistantAgentRunConfiguration");
        }
    }
}
