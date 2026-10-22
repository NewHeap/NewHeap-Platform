using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NewHeap.Platform.AI.Chat.PostgreSql.Migrations
{
    /// <inheritdoc />
    public partial class AcceptsFiles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AcceptsFiles",
                schema: "nhai",
                table: "AssistantAgentSkillDefinition",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "AcceptsFiles",
                schema: "nhai",
                table: "AssistantAgentRunConfiguration",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AcceptsFiles",
                schema: "nhai",
                table: "AssistantAgentSkillDefinition");

            migrationBuilder.DropColumn(
                name: "AcceptsFiles",
                schema: "nhai",
                table: "AssistantAgentRunConfiguration");
        }
    }
}
