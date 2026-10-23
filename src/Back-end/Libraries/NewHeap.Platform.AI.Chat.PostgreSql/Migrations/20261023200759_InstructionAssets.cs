using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NewHeap.Platform.AI.Chat.PostgreSql.Migrations
{
    /// <inheritdoc />
    public partial class InstructionAssets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "InstructionsAssetId",
                schema: "nhai",
                table: "AssistantAgentSkillDefinition",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "InstructionsAssetVersion",
                schema: "nhai",
                table: "AssistantAgentSkillDefinition",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "InstructionsFromAsset",
                schema: "nhai",
                table: "AssistantAgent",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "InstructionsAssetId",
                schema: "nhai",
                table: "AssistantAgentSkillDefinition");

            migrationBuilder.DropColumn(
                name: "InstructionsAssetVersion",
                schema: "nhai",
                table: "AssistantAgentSkillDefinition");

            migrationBuilder.DropColumn(
                name: "InstructionsFromAsset",
                schema: "nhai",
                table: "AssistantAgent");
        }
    }
}
