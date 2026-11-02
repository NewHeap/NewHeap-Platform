using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NewHeap.Platform.AI.Chat.PostgreSql.Migrations
{
    /// <inheritdoc />
    public partial class AlertTimingAndMutes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DigestTimes",
                schema: "nhai",
                table: "AssistantNotificationPreference",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<TimeOnly>(
                name: "QuietHoursEnd",
                schema: "nhai",
                table: "AssistantNotificationPreference",
                type: "time without time zone",
                nullable: true);

            migrationBuilder.AddColumn<TimeOnly>(
                name: "QuietHoursStart",
                schema: "nhai",
                table: "AssistantNotificationPreference",
                type: "time without time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "AssistantAlertSubjectMute",
                schema: "nhai",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ActorId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    KindId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    SubjectType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    SubjectId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    SubjectDisplayName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    MutedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AssistantAlertSubjectMute", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AssistantAlertSubjectMute_ActorId_KindId_SubjectType_Subjec~",
                schema: "nhai",
                table: "AssistantAlertSubjectMute",
                columns: new[] { "ActorId", "KindId", "SubjectType", "SubjectId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AssistantAlertSubjectMute_Until",
                schema: "nhai",
                table: "AssistantAlertSubjectMute",
                column: "Until");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AssistantAlertSubjectMute",
                schema: "nhai");

            migrationBuilder.DropColumn(
                name: "DigestTimes",
                schema: "nhai",
                table: "AssistantNotificationPreference");

            migrationBuilder.DropColumn(
                name: "QuietHoursEnd",
                schema: "nhai",
                table: "AssistantNotificationPreference");

            migrationBuilder.DropColumn(
                name: "QuietHoursStart",
                schema: "nhai",
                table: "AssistantNotificationPreference");
        }
    }
}
