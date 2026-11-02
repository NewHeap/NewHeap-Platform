using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NewHeap.Platform.AI.Chat.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class AgentToolWithoutApproval : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "WithoutApprovalVersion",
                schema: "nhai",
                table: "AssistantToolInvocation",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "WithoutApprovalVersion",
                schema: "nhai",
                table: "AssistantApproval",
                type: "int",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "AssistantAgentToolSwitch",
                schema: "nhai",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AgentId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    ToolId = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    Version = table.Column<int>(type: "int", nullable: false),
                    Mode = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    ContractHash = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    ChangedBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    ChangedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AssistantAgentToolSwitch", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AssistantAgentToolSwitch_AssistantAgent_AgentId",
                        column: x => x.AgentId,
                        principalSchema: "nhai",
                        principalTable: "AssistantAgent",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AssistantAgentToolSwitch_AgentId_ToolId_Version",
                schema: "nhai",
                table: "AssistantAgentToolSwitch",
                columns: new[] { "AgentId", "ToolId", "Version" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AssistantAgentToolSwitch",
                schema: "nhai");

            migrationBuilder.DropColumn(
                name: "WithoutApprovalVersion",
                schema: "nhai",
                table: "AssistantToolInvocation");

            migrationBuilder.DropColumn(
                name: "WithoutApprovalVersion",
                schema: "nhai",
                table: "AssistantApproval");
        }
    }
}
