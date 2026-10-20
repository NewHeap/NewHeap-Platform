using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NewHeap.Platform.AI.Chat.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class AgentBoards : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AssistantAgentGroupMember",
                schema: "nhai",
                columns: table => new
                {
                    AgentId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    GroupId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    IsMember = table.Column<bool>(type: "bit", nullable: false),
                    ChangedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ChangedBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AssistantAgentGroupMember", x => new { x.AgentId, x.GroupId });
                    table.ForeignKey(
                        name: "FK_AssistantAgentGroupMember_AssistantAgent_AgentId",
                        column: x => x.AgentId,
                        principalSchema: "nhai",
                        principalTable: "AssistantAgent",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AssistantBoardVisit",
                schema: "nhai",
                columns: table => new
                {
                    ActorId = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    GroupId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    ScopeKey = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    VisitedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AssistantBoardVisit", x => new { x.ActorId, x.GroupId, x.ScopeKey });
                });

            migrationBuilder.CreateIndex(
                name: "IX_AssistantAgentGroupMember_GroupId",
                schema: "nhai",
                table: "AssistantAgentGroupMember",
                column: "GroupId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AssistantAgentGroupMember",
                schema: "nhai");

            migrationBuilder.DropTable(
                name: "AssistantBoardVisit",
                schema: "nhai");
        }
    }
}
