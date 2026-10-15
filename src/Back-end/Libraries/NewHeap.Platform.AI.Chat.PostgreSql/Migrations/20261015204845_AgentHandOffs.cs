using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NewHeap.Platform.AI.Chat.PostgreSql.Migrations
{
    /// <inheritdoc />
    public partial class AgentHandOffs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "HandOffDepth",
                schema: "nhai",
                table: "AssistantRun",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "RequestedByAgentId",
                schema: "nhai",
                table: "AssistantRun",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "RequestedByRunId",
                schema: "nhai",
                table: "AssistantRun",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "UsedRestrictedResults",
                schema: "nhai",
                table: "AssistantRun",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "AssistantHandOff",
                schema: "nhai",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ScopeKey = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    SourceAgentId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    SourceRunId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceConversationId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceSkillId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    TargetAgentId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    SubjectType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    SubjectId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Depth = table.Column<int>(type: "integer", nullable: false),
                    InputId = table.Column<Guid>(type: "uuid", nullable: false),
                    TargetConversationId = table.Column<Guid>(type: "uuid", nullable: true),
                    TargetRunId = table.Column<Guid>(type: "uuid", nullable: true),
                    Outcome = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Code = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    NotifySource = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    NotifiedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AssistantHandOff", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AssistantHandOff_AssistantConversation_SourceConversationId",
                        column: x => x.SourceConversationId,
                        principalSchema: "nhai",
                        principalTable: "AssistantConversation",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AssistantHandOff_InputId",
                schema: "nhai",
                table: "AssistantHandOff",
                column: "InputId");

            migrationBuilder.CreateIndex(
                name: "IX_AssistantHandOff_SourceAgentId_TargetAgentId_SubjectType_Su~",
                schema: "nhai",
                table: "AssistantHandOff",
                columns: new[] { "SourceAgentId", "TargetAgentId", "SubjectType", "SubjectId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AssistantHandOff_SourceConversationId",
                schema: "nhai",
                table: "AssistantHandOff",
                column: "SourceConversationId");

            migrationBuilder.CreateIndex(
                name: "IX_AssistantHandOff_SourceRunId_TargetAgentId_SubjectType_Subj~",
                schema: "nhai",
                table: "AssistantHandOff",
                columns: new[] { "SourceRunId", "TargetAgentId", "SubjectType", "SubjectId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AssistantHandOff_TargetRunId",
                schema: "nhai",
                table: "AssistantHandOff",
                column: "TargetRunId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AssistantHandOff",
                schema: "nhai");

            migrationBuilder.DropColumn(
                name: "HandOffDepth",
                schema: "nhai",
                table: "AssistantRun");

            migrationBuilder.DropColumn(
                name: "RequestedByAgentId",
                schema: "nhai",
                table: "AssistantRun");

            migrationBuilder.DropColumn(
                name: "RequestedByRunId",
                schema: "nhai",
                table: "AssistantRun");

            migrationBuilder.DropColumn(
                name: "UsedRestrictedResults",
                schema: "nhai",
                table: "AssistantRun");
        }
    }
}
