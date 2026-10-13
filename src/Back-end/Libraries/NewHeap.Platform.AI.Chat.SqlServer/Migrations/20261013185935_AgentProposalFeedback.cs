using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NewHeap.Platform.AI.Chat.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class AgentProposalFeedback : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AssistantProposalFeedback",
                schema: "nhai",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ApprovalId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TargetKind = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    ConversationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ScopeKey = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    AgentId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    AgentVersion = table.Column<int>(type: "int", nullable: true),
                    ToolId = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    ToolContractHash = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    SubjectType = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    SubjectId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ApprovalStatus = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Category = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Text = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    AuthorActorId = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    Bulk = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AssistantProposalFeedback", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AssistantProposalFeedback_AssistantApproval_ApprovalId",
                        column: x => x.ApprovalId,
                        principalSchema: "nhai",
                        principalTable: "AssistantApproval",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AssistantProposalFeedback_AgentId_ToolId",
                schema: "nhai",
                table: "AssistantProposalFeedback",
                columns: new[] { "AgentId", "ToolId" });

            migrationBuilder.CreateIndex(
                name: "IX_AssistantProposalFeedback_ApprovalId_AuthorActorId",
                schema: "nhai",
                table: "AssistantProposalFeedback",
                columns: new[] { "ApprovalId", "AuthorActorId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AssistantProposalFeedback_ScopeKey_AgentId_CreatedAt",
                schema: "nhai",
                table: "AssistantProposalFeedback",
                columns: new[] { "ScopeKey", "AgentId", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AssistantProposalFeedback",
                schema: "nhai");
        }
    }
}
