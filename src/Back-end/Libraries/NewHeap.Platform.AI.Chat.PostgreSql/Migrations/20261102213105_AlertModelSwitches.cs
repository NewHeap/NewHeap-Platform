using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NewHeap.Platform.AI.Chat.PostgreSql.Migrations
{
    /// <inheritdoc />
    public partial class AlertModelSwitches : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "RaisedByModel",
                schema: "nhai",
                table: "AssistantAlert",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "AssistantAgentAlertModelSwitch",
                schema: "nhai",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AgentId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    KindId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    Mode = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    ContractHash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    ChangedBy = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    ChangedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AssistantAgentAlertModelSwitch", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AssistantAgentAlertModelSwitch_AssistantAgent_AgentId",
                        column: x => x.AgentId,
                        principalSchema: "nhai",
                        principalTable: "AssistantAgent",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AssistantAlert_RunId",
                schema: "nhai",
                table: "AssistantAlert",
                column: "RunId");

            migrationBuilder.CreateIndex(
                name: "IX_AssistantAgentAlertModelSwitch_AgentId_KindId_Version",
                schema: "nhai",
                table: "AssistantAgentAlertModelSwitch",
                columns: new[] { "AgentId", "KindId", "Version" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AssistantAgentAlertModelSwitch",
                schema: "nhai");

            migrationBuilder.DropIndex(
                name: "IX_AssistantAlert_RunId",
                schema: "nhai",
                table: "AssistantAlert");

            migrationBuilder.DropColumn(
                name: "RaisedByModel",
                schema: "nhai",
                table: "AssistantAlert");
        }
    }
}
