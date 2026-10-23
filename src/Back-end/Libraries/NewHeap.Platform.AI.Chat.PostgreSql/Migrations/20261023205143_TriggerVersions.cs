using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NewHeap.Platform.AI.Chat.PostgreSql.Migrations
{
    /// <inheritdoc />
    public partial class TriggerVersions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_AssistantAgentTriggerDefinition",
                schema: "nhai",
                table: "AssistantAgentTriggerDefinition");

            migrationBuilder.AddColumn<bool>(
                name: "TriggerCandidate",
                schema: "nhai",
                table: "AssistantRun",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "TriggerVersion",
                schema: "nhai",
                table: "AssistantRun",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "TriggerCandidate",
                schema: "nhai",
                table: "AssistantQueuedInput",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "Status",
                schema: "nhai",
                table: "AssistantAgentTriggerDefinition",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "live");

            migrationBuilder.AddColumn<bool>(
                name: "Off",
                schema: "nhai",
                table: "AssistantAgentConsultationDefinition",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddPrimaryKey(
                name: "PK_AssistantAgentTriggerDefinition",
                schema: "nhai",
                table: "AssistantAgentTriggerDefinition",
                columns: new[] { "Id", "Version" });

            migrationBuilder.CreateIndex(
                name: "IX_AssistantAgentTriggerDefinition_AgentId_Status",
                schema: "nhai",
                table: "AssistantAgentTriggerDefinition",
                columns: new[] { "AgentId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_AssistantAgentTriggerDefinition_Id_Status",
                schema: "nhai",
                table: "AssistantAgentTriggerDefinition",
                columns: new[] { "Id", "Status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_AssistantAgentTriggerDefinition",
                schema: "nhai",
                table: "AssistantAgentTriggerDefinition");

            migrationBuilder.DropIndex(
                name: "IX_AssistantAgentTriggerDefinition_AgentId_Status",
                schema: "nhai",
                table: "AssistantAgentTriggerDefinition");

            migrationBuilder.DropIndex(
                name: "IX_AssistantAgentTriggerDefinition_Id_Status",
                schema: "nhai",
                table: "AssistantAgentTriggerDefinition");

            migrationBuilder.DropColumn(
                name: "TriggerCandidate",
                schema: "nhai",
                table: "AssistantRun");

            migrationBuilder.DropColumn(
                name: "TriggerVersion",
                schema: "nhai",
                table: "AssistantRun");

            migrationBuilder.DropColumn(
                name: "TriggerCandidate",
                schema: "nhai",
                table: "AssistantQueuedInput");

            migrationBuilder.DropColumn(
                name: "Status",
                schema: "nhai",
                table: "AssistantAgentTriggerDefinition");

            migrationBuilder.DropColumn(
                name: "Off",
                schema: "nhai",
                table: "AssistantAgentConsultationDefinition");

            migrationBuilder.AddPrimaryKey(
                name: "PK_AssistantAgentTriggerDefinition",
                schema: "nhai",
                table: "AssistantAgentTriggerDefinition",
                column: "Id");
        }
    }
}
