using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NewHeap.Platform.AI.Chat.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class RequestAnswersAndRequestsOnly : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "AskingConversationId",
                schema: "nhai",
                table: "AssistantRun",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "AskingTurnId",
                schema: "nhai",
                table: "AssistantRun",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "RequestsOnly",
                schema: "nhai",
                table: "AssistantAgentConsultationDefinition",
                type: "bit",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AskingConversationId",
                schema: "nhai",
                table: "AssistantRun");

            migrationBuilder.DropColumn(
                name: "AskingTurnId",
                schema: "nhai",
                table: "AssistantRun");

            migrationBuilder.DropColumn(
                name: "RequestsOnly",
                schema: "nhai",
                table: "AssistantAgentConsultationDefinition");
        }
    }
}
