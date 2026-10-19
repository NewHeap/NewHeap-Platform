using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NewHeap.Platform.AI.Chat.PostgreSql.Migrations
{
    /// <inheritdoc />
    public partial class McpConnectionPolicy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "HeadlessUse",
                schema: "nhai",
                table: "AssistantMcpServer",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "never");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastConnectAt",
                schema: "nhai",
                table: "AssistantMcpServer",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastConnectCode",
                schema: "nhai",
                table: "AssistantMcpServer",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "HeadlessUse",
                schema: "nhai",
                table: "AssistantMcpServer");

            migrationBuilder.DropColumn(
                name: "LastConnectAt",
                schema: "nhai",
                table: "AssistantMcpServer");

            migrationBuilder.DropColumn(
                name: "LastConnectCode",
                schema: "nhai",
                table: "AssistantMcpServer");
        }
    }
}
