using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WebAPI.DAL.Migrations
{
    /// <inheritdoc />
    public partial class UserNotifications : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_NhNotificationDeliveries_Notifications_NotificationId",
                table: "NhNotificationDeliveries");

            migrationBuilder.DropPrimaryKey(
                name: "PK_NhNotificationDeliveries",
                table: "NhNotificationDeliveries");

            migrationBuilder.RenameTable(
                name: "NhNotificationDeliveries",
                newName: "NotificationDeliveries");

            migrationBuilder.RenameIndex(
                name: "IX_NhNotificationDeliveries_NotificationId",
                table: "NotificationDeliveries",
                newName: "IX_NotificationDeliveries_NotificationId");

            migrationBuilder.AddPrimaryKey(
                name: "PK_NotificationDeliveries",
                table: "NotificationDeliveries",
                column: "Id");

            migrationBuilder.CreateTable(
                name: "UserNotifications",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreationDateTime = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    LastModifiedDateTime = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LastTitle = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    LastMessage = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    IsLastRead = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserNotifications", x => x.Id);
                    table.ForeignKey(
                        name: "FK_UserNotifications_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "UserNotificationMessages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreationDateTime = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    LastModifiedDateTime = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Title = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    Message = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    UserNotificationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserNotificationMessages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_UserNotificationMessages_UserNotifications_UserNotificationId",
                        column: x => x.UserNotificationId,
                        principalTable: "UserNotifications",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_UserNotificationMessages_UserNotificationId",
                table: "UserNotificationMessages",
                column: "UserNotificationId");

            migrationBuilder.CreateIndex(
                name: "IX_UserNotifications_UserId",
                table: "UserNotifications",
                column: "UserId");

            migrationBuilder.AddForeignKey(
                name: "FK_NotificationDeliveries_Notifications_NotificationId",
                table: "NotificationDeliveries",
                column: "NotificationId",
                principalTable: "Notifications",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_NotificationDeliveries_Notifications_NotificationId",
                table: "NotificationDeliveries");

            migrationBuilder.DropTable(
                name: "UserNotificationMessages");

            migrationBuilder.DropTable(
                name: "UserNotifications");

            migrationBuilder.DropPrimaryKey(
                name: "PK_NotificationDeliveries",
                table: "NotificationDeliveries");

            migrationBuilder.RenameTable(
                name: "NotificationDeliveries",
                newName: "NhNotificationDeliveries");

            migrationBuilder.RenameIndex(
                name: "IX_NotificationDeliveries_NotificationId",
                table: "NhNotificationDeliveries",
                newName: "IX_NhNotificationDeliveries_NotificationId");

            migrationBuilder.AddPrimaryKey(
                name: "PK_NhNotificationDeliveries",
                table: "NhNotificationDeliveries",
                column: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_NhNotificationDeliveries_Notifications_NotificationId",
                table: "NhNotificationDeliveries",
                column: "NotificationId",
                principalTable: "Notifications",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
