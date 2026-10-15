using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NewHeap.Platform.AI.Chat.PostgreSql.Migrations
{
    /// <inheritdoc />
    public partial class AgentEvaluation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ModelCalls",
                schema: "nhai",
                table: "AssistantRun",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<Guid>(
                name: "ReplayOfRunId",
                schema: "nhai",
                table: "AssistantRun",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "AssistantAgentReplay",
                schema: "nhai",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    VariantAgentId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    SourceAgentId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    From = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    To = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    MaxRuns = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    StopReason = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    RunsReplayed = table.Column<int>(type: "integer", nullable: false),
                    RunsSkipped = table.Column<int>(type: "integer", nullable: false),
                    CursorCreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CursorRunId = table.Column<Guid>(type: "uuid", nullable: true),
                    RequestedByActorId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    EndedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ConcurrencyStamp = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AssistantAgentReplay", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AssistantRun_ReplayOfRunId",
                schema: "nhai",
                table: "AssistantRun",
                column: "ReplayOfRunId");

            migrationBuilder.CreateIndex(
                name: "IX_AssistantAgentReplay_VariantAgentId_CreatedAt",
                schema: "nhai",
                table: "AssistantAgentReplay",
                columns: new[] { "VariantAgentId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AssistantAgentReplay_VariantAgentId_Status",
                schema: "nhai",
                table: "AssistantAgentReplay",
                columns: new[] { "VariantAgentId", "Status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AssistantAgentReplay",
                schema: "nhai");

            migrationBuilder.DropIndex(
                name: "IX_AssistantRun_ReplayOfRunId",
                schema: "nhai",
                table: "AssistantRun");

            migrationBuilder.DropColumn(
                name: "ModelCalls",
                schema: "nhai",
                table: "AssistantRun");

            migrationBuilder.DropColumn(
                name: "ReplayOfRunId",
                schema: "nhai",
                table: "AssistantRun");
        }
    }
}
