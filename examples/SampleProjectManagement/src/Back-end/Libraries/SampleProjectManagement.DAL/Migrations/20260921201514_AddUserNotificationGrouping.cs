using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SampleProjectManagement.DAL.Migrations
{
    /// <inheritdoc />
    public partial class AddUserNotificationGrouping : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_UserNotifications_UserId",
                table: "UserNotifications");

            migrationBuilder.AddColumn<string>(
                name: "Category",
                table: "UserNotifications",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "GroupKey",
                table: "UserNotifications",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Severity",
                table: "UserNotifications",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "Severity",
                table: "UserNotificationMessages",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "IX_UserNotifications_UserId_GroupKey",
                table: "UserNotifications",
                columns: new[] { "UserId", "GroupKey" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_UserNotifications_UserId_GroupKey",
                table: "UserNotifications");

            migrationBuilder.DropColumn(
                name: "Category",
                table: "UserNotifications");

            migrationBuilder.DropColumn(
                name: "GroupKey",
                table: "UserNotifications");

            migrationBuilder.DropColumn(
                name: "Severity",
                table: "UserNotifications");

            migrationBuilder.DropColumn(
                name: "Severity",
                table: "UserNotificationMessages");

            migrationBuilder.CreateIndex(
                name: "IX_UserNotifications_UserId",
                table: "UserNotifications",
                column: "UserId");
        }
    }
}
