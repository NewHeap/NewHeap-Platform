using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NewHeap.Platform.AI.Chat.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class HandOffsAsData : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ShadowOfAgentId",
                schema: "nhai",
                table: "AssistantAgent",
                type: "nvarchar(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "AssistantAgentHandOffDefinition",
                schema: "nhai",
                columns: table => new
                {
                    AgentId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    SubjectType = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Version = table.Column<int>(type: "int", nullable: false),
                    SourcesJson = table.Column<string>(type: "nvarchar(2700)", maxLength: 2700, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    NotifySource = table.Column<bool>(type: "bit", nullable: false),
                    MaxPerSubjectPerDay = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    ConcurrencyStamp = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AssistantAgentHandOffDefinition", x => new { x.AgentId, x.SubjectType });
                    table.ForeignKey(
                        name: "FK_AssistantAgentHandOffDefinition_AssistantAgent_AgentId",
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
                name: "AssistantAgentHandOffDefinition",
                schema: "nhai");

            migrationBuilder.DropColumn(
                name: "ShadowOfAgentId",
                schema: "nhai",
                table: "AssistantAgent");
        }
    }
}
