using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NewHeap.Platform.AI.Chat.PostgreSql.Migrations
{
    /// <inheritdoc />
    public partial class AgentRunConfiguration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AssistantAgentSkillDefinition",
                schema: "nhai",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AgentId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    SkillId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Description = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    Instructions = table.Column<string>(type: "character varying(20000)", maxLength: 20000, nullable: true),
                    InstructionsHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ToolSelectorsJson = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: false),
                    ProfileName = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    RuleHandlerName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    MaxTokensPerRun = table.Column<long>(type: "bigint", nullable: true),
                    DailyToolCalls = table.Column<int>(type: "integer", nullable: true),
                    DailyTokens = table.Column<long>(type: "bigint", nullable: true),
                    ApprovalPolicyId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AssistantAgentSkillDefinition", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AssistantAgentSkillDefinition_AssistantAgent_AgentId",
                        column: x => x.AgentId,
                        principalSchema: "nhai",
                        principalTable: "AssistantAgent",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AssistantAgentTriggerDefinition",
                schema: "nhai",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    AgentId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    SkillId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Source = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    EventName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Cron = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    TimeZone = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    SubjectSetName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Message = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    Conflict = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    DebounceSeconds = table.Column<int>(type: "integer", nullable: false),
                    Shadow = table.Column<bool>(type: "boolean", nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedBy = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    ConcurrencyStamp = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AssistantAgentTriggerDefinition", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AssistantAgentTriggerDefinition_AssistantAgent_AgentId",
                        column: x => x.AgentId,
                        principalSchema: "nhai",
                        principalTable: "AssistantAgent",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AssistantRunConfigurationStamp",
                schema: "nhai",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false),
                    Value = table.Column<Guid>(type: "uuid", nullable: false),
                    ChangedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AssistantRunConfigurationStamp", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AssistantAgentSkillDefinition_AgentId_SkillId_Version",
                schema: "nhai",
                table: "AssistantAgentSkillDefinition",
                columns: new[] { "AgentId", "SkillId", "Version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AssistantAgentSkillDefinition_AgentId_Status",
                schema: "nhai",
                table: "AssistantAgentSkillDefinition",
                columns: new[] { "AgentId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_AssistantAgentTriggerDefinition_AgentId",
                schema: "nhai",
                table: "AssistantAgentTriggerDefinition",
                column: "AgentId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AssistantAgentSkillDefinition",
                schema: "nhai");

            migrationBuilder.DropTable(
                name: "AssistantAgentTriggerDefinition",
                schema: "nhai");

            migrationBuilder.DropTable(
                name: "AssistantRunConfigurationStamp",
                schema: "nhai");
        }
    }
}
