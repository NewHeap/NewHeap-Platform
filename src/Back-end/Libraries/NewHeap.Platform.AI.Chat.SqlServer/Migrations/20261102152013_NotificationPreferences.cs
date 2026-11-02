using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NewHeap.Platform.AI.Chat.SqlServer.Migrations
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
                    ActorId = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    Category = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    KindId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Muted = table.Column<bool>(type: "bit", nullable: false),
                    DigestTime = table.Column<TimeOnly>(type: "time", nullable: true),
                    Push = table.Column<bool>(type: "bit", nullable: true),
                    InApp = table.Column<bool>(type: "bit", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
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
