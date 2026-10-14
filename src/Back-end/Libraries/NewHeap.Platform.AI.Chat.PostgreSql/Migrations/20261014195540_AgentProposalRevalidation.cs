using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NewHeap.Platform.AI.Chat.PostgreSql.Migrations
{
    /// <inheritdoc />
    public partial class AgentProposalRevalidation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "StaleExplanationsJson",
                schema: "nhai",
                table: "AssistantApproval",
                type: "character varying(6000)",
                maxLength: 6000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "StaleKind",
                schema: "nhai",
                table: "AssistantApproval",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "AssistantApprovalKey",
                schema: "nhai",
                columns: table => new
                {
                    ApprovalId = table.Column<Guid>(type: "uuid", nullable: false),
                    KeyType = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    KeyId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    ScopeKey = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AssistantApprovalKey", x => new { x.ApprovalId, x.KeyType, x.KeyId });
                    table.ForeignKey(
                        name: "FK_AssistantApprovalKey_AssistantApproval_ApprovalId",
                        column: x => x.ApprovalId,
                        principalSchema: "nhai",
                        principalTable: "AssistantApproval",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AssistantApprovalKey_ScopeKey_KeyType_KeyId",
                schema: "nhai",
                table: "AssistantApprovalKey",
                columns: new[] { "ScopeKey", "KeyType", "KeyId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AssistantApprovalKey",
                schema: "nhai");

            migrationBuilder.DropColumn(
                name: "StaleExplanationsJson",
                schema: "nhai",
                table: "AssistantApproval");

            migrationBuilder.DropColumn(
                name: "StaleKind",
                schema: "nhai",
                table: "AssistantApproval");
        }
    }
}
