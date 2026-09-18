using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NewHeap.Platform.AI.Chat.PostgreSql.Migrations
{
    /// <inheritdoc />
    public partial class AdminAndPreferences : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AssistantAgent",
                schema: "nhai",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    Source = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    IsOverridden = table.Column<bool>(type: "boolean", nullable: false),
                    DisplayName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Description = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    ProfileName = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Instructions = table.Column<string>(type: "text", nullable: false),
                    InstructionsAssetId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    InstructionsAssetVersion = table.Column<int>(type: "integer", nullable: false),
                    InstructionsHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ToolSelectorsJson = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: false),
                    RequiredPolicy = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    Autonomy = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    IsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    CodeHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedBy = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AssistantAgent", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AssistantApplicationContext",
                schema: "nhai",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    Text = table.Column<string>(type: "character varying(20000)", maxLength: 20000, nullable: false),
                    Hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedBy = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AssistantApplicationContext", x => new { x.Id, x.Version });
                });

            migrationBuilder.CreateTable(
                name: "AssistantMcpServer",
                schema: "nhai",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    DisplayName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Url = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    AuthMode = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    HeaderName = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    ProtectedSecret = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    RequiredPolicy = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    IsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    LastSyncAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastSyncStatus = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AssistantMcpServer", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AssistantUserPreference",
                schema: "nhai",
                columns: table => new
                {
                    ActorId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Style = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    AddressForm = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    ResponseLength = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    CustomInstructions = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AssistantUserPreference", x => x.ActorId);
                });

            migrationBuilder.CreateTable(
                name: "AssistantAgentMcpServer",
                schema: "nhai",
                columns: table => new
                {
                    AgentId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    McpServerId = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AssistantAgentMcpServer", x => new { x.AgentId, x.McpServerId });
                    table.ForeignKey(
                        name: "FK_AssistantAgentMcpServer_AssistantAgent_AgentId",
                        column: x => x.AgentId,
                        principalSchema: "nhai",
                        principalTable: "AssistantAgent",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AssistantAgentMcpServer_AssistantMcpServer_McpServerId",
                        column: x => x.McpServerId,
                        principalSchema: "nhai",
                        principalTable: "AssistantMcpServer",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AssistantMcpTool",
                schema: "nhai",
                columns: table => new
                {
                    ServerId = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    RemoteName = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    LocalId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    InputSchemaHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    IsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    Effect = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    DescriptionOverride = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ReadOnlyHint = table.Column<bool>(type: "boolean", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AssistantMcpTool", x => new { x.ServerId, x.RemoteName });
                    table.ForeignKey(
                        name: "FK_AssistantMcpTool_AssistantMcpServer_ServerId",
                        column: x => x.ServerId,
                        principalSchema: "nhai",
                        principalTable: "AssistantMcpServer",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AssistantAgentMcpServer_McpServerId",
                schema: "nhai",
                table: "AssistantAgentMcpServer",
                column: "McpServerId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AssistantAgentMcpServer",
                schema: "nhai");

            migrationBuilder.DropTable(
                name: "AssistantApplicationContext",
                schema: "nhai");

            migrationBuilder.DropTable(
                name: "AssistantMcpTool",
                schema: "nhai");

            migrationBuilder.DropTable(
                name: "AssistantUserPreference",
                schema: "nhai");

            migrationBuilder.DropTable(
                name: "AssistantAgent",
                schema: "nhai");

            migrationBuilder.DropTable(
                name: "AssistantMcpServer",
                schema: "nhai");
        }
    }
}
