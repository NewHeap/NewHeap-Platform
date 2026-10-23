using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NewHeap.Platform.AI.Chat.PostgreSql.Migrations
{
    /// <inheritdoc />
    public partial class AgentConsultationDefinitions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ConsultationVersion",
                schema: "nhai",
                table: "AssistantConsultation",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "AssistantAgentConsultationDefinition",
                schema: "nhai",
                columns: table => new
                {
                    AgentId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    SubjectType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    Description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    Instructions = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    InstructionsHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    MinimumRole = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    AskerPolicy = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    RoutesJson = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    ProposalRole = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    ProposalPolicyId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    MaxToolCalls = table.Column<int>(type: "integer", nullable: false),
                    TimeoutSeconds = table.Column<int>(type: "integer", nullable: false),
                    MaxAnswerChars = table.Column<int>(type: "integer", nullable: false),
                    MaxHistoryMessages = table.Column<int>(type: "integer", nullable: false),
                    AllowRequests = table.Column<bool>(type: "boolean", nullable: false),
                    ExposeToMcp = table.Column<bool>(type: "boolean", nullable: false),
                    DataClassification = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedBy = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    ConcurrencyStamp = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AssistantAgentConsultationDefinition", x => new { x.AgentId, x.SubjectType });
                    table.ForeignKey(
                        name: "FK_AssistantAgentConsultationDefinition_AssistantAgent_AgentId",
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
                name: "AssistantAgentConsultationDefinition",
                schema: "nhai");

            migrationBuilder.DropColumn(
                name: "ConsultationVersion",
                schema: "nhai",
                table: "AssistantConsultation");
        }
    }
}
