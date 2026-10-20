using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NewHeap.Platform.AI.Chat.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class McpRunConnectionSkips : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "McpConnectionSkips",
                schema: "nhai",
                table: "AssistantRun",
                type: "nvarchar(max)",
                maxLength: 16000,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "AssistantMcpConnectionNotice",
                schema: "nhai",
                columns: table => new
                {
                    ServerId = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    PersonActorId = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    Code = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    NotifiedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AssistantMcpConnectionNotice", x => new { x.ServerId, x.PersonActorId });
                    table.ForeignKey(
                        name: "FK_AssistantMcpConnectionNotice_AssistantMcpServer_ServerId",
                        column: x => x.ServerId,
                        principalSchema: "nhai",
                        principalTable: "AssistantMcpServer",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AssistantMcpConnectionNotice_NotifiedAt",
                schema: "nhai",
                table: "AssistantMcpConnectionNotice",
                column: "NotifiedAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AssistantMcpConnectionNotice",
                schema: "nhai");

            migrationBuilder.DropColumn(
                name: "McpConnectionSkips",
                schema: "nhai",
                table: "AssistantRun");
        }
    }
}
