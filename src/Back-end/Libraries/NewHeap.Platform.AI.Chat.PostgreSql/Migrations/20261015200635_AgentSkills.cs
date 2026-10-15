using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NewHeap.Platform.AI.Chat.PostgreSql.Migrations
{
    /// <inheritdoc />
    public partial class AgentSkills : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SkillChosenBy",
                schema: "nhai",
                table: "AssistantRun",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SkillId",
                schema: "nhai",
                table: "AssistantRun",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SkillId",
                schema: "nhai",
                table: "AssistantQueuedInput",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "AssistantAgentSkillState",
                schema: "nhai",
                columns: table => new
                {
                    AgentId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    SkillId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    RunState = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    StateChangedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    StateChangedBy = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    StateReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AssistantAgentSkillState", x => new { x.AgentId, x.SkillId });
                    table.ForeignKey(
                        name: "FK_AssistantAgentSkillState_AssistantAgent_AgentId",
                        column: x => x.AgentId,
                        principalSchema: "nhai",
                        principalTable: "AssistantAgent",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AssistantAgentSkillState",
                schema: "nhai");

            migrationBuilder.DropColumn(
                name: "SkillChosenBy",
                schema: "nhai",
                table: "AssistantRun");

            migrationBuilder.DropColumn(
                name: "SkillId",
                schema: "nhai",
                table: "AssistantRun");

            migrationBuilder.DropColumn(
                name: "SkillId",
                schema: "nhai",
                table: "AssistantQueuedInput");
        }
    }
}
