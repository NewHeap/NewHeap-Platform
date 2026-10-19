using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NewHeap.Platform.AI.Chat.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class AgentRunSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AssistantAgentRunConfiguration",
                schema: "nhai",
                columns: table => new
                {
                    AgentId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    Version = table.Column<int>(type: "int", nullable: false),
                    Accountability = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    ServiceAccountActorId = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    DelegateFromInitiator = table.Column<bool>(type: "bit", nullable: false),
                    AllowGatewayReads = table.Column<bool>(type: "bit", nullable: false),
                    ElevatedReads = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    ApprovalPolicyId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    RetryCount = table.Column<int>(type: "int", nullable: false),
                    DailyToolCalls = table.Column<int>(type: "int", nullable: true),
                    DailyTokens = table.Column<long>(type: "bigint", nullable: true),
                    MaxConcurrentRuns = table.Column<int>(type: "int", nullable: false),
                    MaxRunsPerSubjectPerDay = table.Column<int>(type: "int", nullable: false),
                    MaxTokensPerRun = table.Column<long>(type: "bigint", nullable: true),
                    TriageProfileName = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    RuleHandlerName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    ConcurrencyStamp = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AssistantAgentRunConfiguration", x => x.AgentId);
                    table.ForeignKey(
                        name: "FK_AssistantAgentRunConfiguration_AssistantAgent_AgentId",
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
                name: "AssistantAgentRunConfiguration",
                schema: "nhai");
        }
    }
}
