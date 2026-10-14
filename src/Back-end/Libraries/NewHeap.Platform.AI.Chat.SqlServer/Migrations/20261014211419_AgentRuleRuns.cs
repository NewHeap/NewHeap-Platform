using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NewHeap.Platform.AI.Chat.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class AgentRuleRuns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Kind",
                schema: "nhai",
                table: "AssistantRun",
                type: "nvarchar(24)",
                maxLength: 24,
                nullable: false,
                defaultValue: "model");

            migrationBuilder.AddColumn<string>(
                name: "PayloadJson",
                schema: "nhai",
                table: "AssistantQueuedInput",
                type: "nvarchar(4000)",
                maxLength: 4000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ArgumentsHash",
                schema: "nhai",
                table: "AssistantApproval",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RationalesJson",
                schema: "nhai",
                table: "AssistantApproval",
                type: "nvarchar(max)",
                maxLength: 6000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "StaleByActorId",
                schema: "nhai",
                table: "AssistantApproval",
                type: "nvarchar(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SupersededByApprovalId",
                schema: "nhai",
                table: "AssistantApproval",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_AssistantApproval_ToolId_ArgumentsHash",
                schema: "nhai",
                table: "AssistantApproval",
                columns: new[] { "ToolId", "ArgumentsHash" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AssistantApproval_ToolId_ArgumentsHash",
                schema: "nhai",
                table: "AssistantApproval");

            migrationBuilder.DropColumn(
                name: "Kind",
                schema: "nhai",
                table: "AssistantRun");

            migrationBuilder.DropColumn(
                name: "PayloadJson",
                schema: "nhai",
                table: "AssistantQueuedInput");

            migrationBuilder.DropColumn(
                name: "ArgumentsHash",
                schema: "nhai",
                table: "AssistantApproval");

            migrationBuilder.DropColumn(
                name: "RationalesJson",
                schema: "nhai",
                table: "AssistantApproval");

            migrationBuilder.DropColumn(
                name: "StaleByActorId",
                schema: "nhai",
                table: "AssistantApproval");

            migrationBuilder.DropColumn(
                name: "SupersededByApprovalId",
                schema: "nhai",
                table: "AssistantApproval");
        }
    }
}
