using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NewHeap.Platform.AI.Chat.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class AgentSkillCandidates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "SkillCandidate",
                schema: "nhai",
                table: "AssistantRun",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "SkillVersion",
                schema: "nhai",
                table: "AssistantRun",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "SkillCandidate",
                schema: "nhai",
                table: "AssistantQueuedInput",
                type: "bit",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SkillCandidate",
                schema: "nhai",
                table: "AssistantRun");

            migrationBuilder.DropColumn(
                name: "SkillVersion",
                schema: "nhai",
                table: "AssistantRun");

            migrationBuilder.DropColumn(
                name: "SkillCandidate",
                schema: "nhai",
                table: "AssistantQueuedInput");
        }
    }
}
