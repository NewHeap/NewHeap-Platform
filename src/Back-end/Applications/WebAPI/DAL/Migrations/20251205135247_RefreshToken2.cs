using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WebAPI.DAL.Migrations
{
    /// <inheritdoc />
    public partial class RefreshToken2 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_UserNotificationMessages_NhUserAuthRefreshToken_NhUserAuthRefreshTokenId",
                table: "UserNotificationMessages");

            migrationBuilder.DropIndex(
                name: "IX_UserNotificationMessages_NhUserAuthRefreshTokenId",
                table: "UserNotificationMessages");

            migrationBuilder.DropColumn(
                name: "NhUserAuthRefreshTokenId",
                table: "UserNotificationMessages");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "NhUserAuthRefreshTokenId",
                table: "UserNotificationMessages",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_UserNotificationMessages_NhUserAuthRefreshTokenId",
                table: "UserNotificationMessages",
                column: "NhUserAuthRefreshTokenId");

            migrationBuilder.AddForeignKey(
                name: "FK_UserNotificationMessages_NhUserAuthRefreshToken_NhUserAuthRefreshTokenId",
                table: "UserNotificationMessages",
                column: "NhUserAuthRefreshTokenId",
                principalTable: "NhUserAuthRefreshToken",
                principalColumn: "Id");
        }
    }
}
