using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NewHeap.Platform.AI.Chat.PostgreSql.Migrations
{
    /// <inheritdoc />
    public partial class AgentState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "EventKey",
                schema: "nhai",
                table: "AssistantQueuedInput",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "InitiatorActorId",
                schema: "nhai",
                table: "AssistantQueuedInput",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RunState",
                schema: "nhai",
                table: "AssistantAgent",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "active");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "StateChangedAt",
                schema: "nhai",
                table: "AssistantAgent",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "StateChangedBy",
                schema: "nhai",
                table: "AssistantAgent",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "StateReason",
                schema: "nhai",
                table: "AssistantAgent",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EventKey",
                schema: "nhai",
                table: "AssistantQueuedInput");

            migrationBuilder.DropColumn(
                name: "InitiatorActorId",
                schema: "nhai",
                table: "AssistantQueuedInput");

            migrationBuilder.DropColumn(
                name: "RunState",
                schema: "nhai",
                table: "AssistantAgent");

            migrationBuilder.DropColumn(
                name: "StateChangedAt",
                schema: "nhai",
                table: "AssistantAgent");

            migrationBuilder.DropColumn(
                name: "StateChangedBy",
                schema: "nhai",
                table: "AssistantAgent");

            migrationBuilder.DropColumn(
                name: "StateReason",
                schema: "nhai",
                table: "AssistantAgent");
        }
    }
}
