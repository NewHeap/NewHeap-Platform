using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NewHeap.Platform.AI.Chat.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class ApplicationExecutions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ExecutionReportDueAt",
                schema: "nhai",
                table: "AssistantApproval",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_AssistantApproval_Status_ExecutionReportDueAt",
                schema: "nhai",
                table: "AssistantApproval",
                columns: new[] { "Status", "ExecutionReportDueAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AssistantApproval_Status_ExecutionReportDueAt",
                schema: "nhai",
                table: "AssistantApproval");

            migrationBuilder.DropColumn(
                name: "ExecutionReportDueAt",
                schema: "nhai",
                table: "AssistantApproval");
        }
    }
}
