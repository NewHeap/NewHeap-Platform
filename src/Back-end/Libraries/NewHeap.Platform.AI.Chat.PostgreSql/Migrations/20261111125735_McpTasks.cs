using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NewHeap.Platform.AI.Chat.PostgreSql.Migrations
{
    /// <inheritdoc />
    public partial class McpTasks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AssistantMcpTask",
                schema: "nhai",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ToolName = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    ToolId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    ToolVersion = table.Column<int>(type: "integer", nullable: false),
                    ActorId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    TenantKey = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    DivisionKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastUpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    AbandonAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    PollIntervalMs = table.Column<long>(type: "bigint", nullable: false),
                    ResultJson = table.Column<string>(type: "text", nullable: true),
                    ErrorJson = table.Column<string>(type: "text", nullable: true),
                    StatusMessage = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AssistantMcpTask", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AssistantMcpTask_ExpiresAt",
                schema: "nhai",
                table: "AssistantMcpTask",
                column: "ExpiresAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AssistantMcpTask",
                schema: "nhai");
        }
    }
}
