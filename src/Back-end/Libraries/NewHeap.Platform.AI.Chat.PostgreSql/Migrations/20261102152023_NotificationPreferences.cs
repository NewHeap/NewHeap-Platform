using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NewHeap.Platform.AI.Chat.PostgreSql.Migrations
{
    /// <inheritdoc />
    public partial class NotificationPreferences : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AssistantNotificationPreference",
                schema: "nhai",
                columns: table => new
                {
                    ActorId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Category = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    KindId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Muted = table.Column<bool>(type: "boolean", nullable: false),
                    DigestTime = table.Column<TimeOnly>(type: "time without time zone", nullable: true),
                    Push = table.Column<bool>(type: "boolean", nullable: true),
                    InApp = table.Column<bool>(type: "boolean", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AssistantNotificationPreference", x => new { x.ActorId, x.Category, x.KindId });
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AssistantNotificationPreference",
                schema: "nhai");
        }
    }
}
