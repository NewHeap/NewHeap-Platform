using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NewHeap.Platform.AI.Chat.PostgreSql.Migrations
{
    /// <inheritdoc />
    public partial class AgentConsultations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ConsultationId",
                schema: "nhai",
                table: "AssistantApproval",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "AssistantConsultation",
                schema: "nhai",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ConversationId = table.Column<Guid>(type: "uuid", nullable: false),
                    ScopeKey = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    AgentId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    SubjectType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    SubjectId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    AskerActorId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    AskingAgentId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    AskingConversationId = table.Column<Guid>(type: "uuid", nullable: true),
                    AskingTurnId = table.Column<Guid>(type: "uuid", nullable: true),
                    Depth = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    FailureCode = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    ToolCalls = table.Column<int>(type: "integer", nullable: false),
                    InputTokens = table.Column<long>(type: "bigint", nullable: false),
                    OutputTokens = table.Column<long>(type: "bigint", nullable: false),
                    ProposalCount = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    EndedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AssistantConsultation", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AssistantConsultation_AssistantConversation_ConversationId",
                        column: x => x.ConversationId,
                        principalSchema: "nhai",
                        principalTable: "AssistantConversation",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AssistantApproval_ConsultationId",
                schema: "nhai",
                table: "AssistantApproval",
                column: "ConsultationId");

            migrationBuilder.CreateIndex(
                name: "IX_AssistantConsultation_AgentId_ScopeKey_CreatedAt",
                schema: "nhai",
                table: "AssistantConsultation",
                columns: new[] { "AgentId", "ScopeKey", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AssistantConsultation_AskerActorId_CreatedAt",
                schema: "nhai",
                table: "AssistantConsultation",
                columns: new[] { "AskerActorId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AssistantConsultation_ConversationId_CreatedAt",
                schema: "nhai",
                table: "AssistantConsultation",
                columns: new[] { "ConversationId", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AssistantConsultation",
                schema: "nhai");

            migrationBuilder.DropIndex(
                name: "IX_AssistantApproval_ConsultationId",
                schema: "nhai",
                table: "AssistantApproval");

            migrationBuilder.DropColumn(
                name: "ConsultationId",
                schema: "nhai",
                table: "AssistantApproval");
        }
    }
}
