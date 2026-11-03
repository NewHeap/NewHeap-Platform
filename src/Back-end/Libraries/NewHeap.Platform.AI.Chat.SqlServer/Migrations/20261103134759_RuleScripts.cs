using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NewHeap.Platform.AI.Chat.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class RuleScripts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AssistantRuleScript",
                schema: "nhai",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AgentId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    SkillId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Revision = table.Column<int>(type: "int", nullable: false),
                    LiveVersion = table.Column<int>(type: "int", nullable: true),
                    CandidateVersion = table.Column<int>(type: "int", nullable: true),
                    CandidateShadow = table.Column<bool>(type: "bit", nullable: false),
                    LatestVersion = table.Column<int>(type: "int", nullable: false),
                    ChangedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ChangedBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AssistantRuleScript", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AssistantRuleScript_AssistantAgent_AgentId",
                        column: x => x.AgentId,
                        principalSchema: "nhai",
                        principalTable: "AssistantAgent",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AssistantRuleScriptVersion",
                schema: "nhai",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RuleScriptId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AgentId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    SkillId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Version = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Script = table.Column<string>(type: "nvarchar(max)", maxLength: 10000, nullable: false),
                    ScriptHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    WritesJson = table.Column<string>(type: "nvarchar(max)", maxLength: 8000, nullable: false),
                    AuthorActorId = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    AuthorAgentId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    Reason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    LiveAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LiveBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    EndedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    EndedBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AssistantRuleScriptVersion", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AssistantRuleScriptVersion_AssistantRuleScript_RuleScriptId",
                        column: x => x.RuleScriptId,
                        principalSchema: "nhai",
                        principalTable: "AssistantRuleScript",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AssistantRuleScriptRunRecord",
                schema: "nhai",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    VersionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AgentId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    SkillId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Version = table.Column<int>(type: "int", nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    RunId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RanBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    RanAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    SubjectCount = table.Column<int>(type: "int", nullable: false),
                    CompletedCount = table.Column<int>(type: "int", nullable: false),
                    RecordJson = table.Column<string>(type: "nvarchar(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AssistantRuleScriptRunRecord", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AssistantRuleScriptRunRecord_AssistantRuleScriptVersion_VersionId",
                        column: x => x.VersionId,
                        principalSchema: "nhai",
                        principalTable: "AssistantRuleScriptVersion",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AssistantRuleScript_AgentId_SkillId",
                schema: "nhai",
                table: "AssistantRuleScript",
                columns: new[] { "AgentId", "SkillId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AssistantRuleScriptRunRecord_VersionId_Kind_RanAt",
                schema: "nhai",
                table: "AssistantRuleScriptRunRecord",
                columns: new[] { "VersionId", "Kind", "RanAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AssistantRuleScriptVersion_AgentId_SkillId_Status",
                schema: "nhai",
                table: "AssistantRuleScriptVersion",
                columns: new[] { "AgentId", "SkillId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_AssistantRuleScriptVersion_RuleScriptId_Version",
                schema: "nhai",
                table: "AssistantRuleScriptVersion",
                columns: new[] { "RuleScriptId", "Version" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AssistantRuleScriptRunRecord",
                schema: "nhai");

            migrationBuilder.DropTable(
                name: "AssistantRuleScriptVersion",
                schema: "nhai");

            migrationBuilder.DropTable(
                name: "AssistantRuleScript",
                schema: "nhai");
        }
    }
}
