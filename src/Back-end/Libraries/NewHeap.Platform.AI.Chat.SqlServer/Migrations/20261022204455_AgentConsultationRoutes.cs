using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NewHeap.Platform.AI.Chat.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class AgentConsultationRoutes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "ConsultAskerDecides",
                schema: "nhai",
                table: "AssistantAgent",
                type: "bit",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ConsultRoutesChangedAt",
                schema: "nhai",
                table: "AssistantAgent",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ConsultRoutesChangedBy",
                schema: "nhai",
                table: "AssistantAgent",
                type: "nvarchar(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ConsultRoutesReason",
                schema: "nhai",
                table: "AssistantAgent",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "ConsultSubjectAgentRights",
                schema: "nhai",
                table: "AssistantAgent",
                type: "bit",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "ConsultSubjectAskerRights",
                schema: "nhai",
                table: "AssistantAgent",
                type: "bit",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ConsultAskerDecides",
                schema: "nhai",
                table: "AssistantAgent");

            migrationBuilder.DropColumn(
                name: "ConsultRoutesChangedAt",
                schema: "nhai",
                table: "AssistantAgent");

            migrationBuilder.DropColumn(
                name: "ConsultRoutesChangedBy",
                schema: "nhai",
                table: "AssistantAgent");

            migrationBuilder.DropColumn(
                name: "ConsultRoutesReason",
                schema: "nhai",
                table: "AssistantAgent");

            migrationBuilder.DropColumn(
                name: "ConsultSubjectAgentRights",
                schema: "nhai",
                table: "AssistantAgent");

            migrationBuilder.DropColumn(
                name: "ConsultSubjectAskerRights",
                schema: "nhai",
                table: "AssistantAgent");
        }
    }
}
