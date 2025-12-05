using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WebAPI.DAL.Migrations
{
    /// <inheritdoc />
    public partial class RefreshToken : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RefreshToken",
                table: "AspNetUsers");

            migrationBuilder.AddColumn<Guid>(
                name: "NhUserAuthRefreshTokenId",
                table: "UserNotificationMessages",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "NhUserAuthRefreshToken",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreationDateTime = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    LastModifiedDateTime = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Token = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    ExpiryDateTime = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NhUserAuthRefreshToken", x => x.Id);
                    table.ForeignKey(
                        name: "FK_NhUserAuthRefreshToken_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_UserNotificationMessages_NhUserAuthRefreshTokenId",
                table: "UserNotificationMessages",
                column: "NhUserAuthRefreshTokenId");

            migrationBuilder.CreateIndex(
                name: "IX_NhUserAuthRefreshToken_Token",
                table: "NhUserAuthRefreshToken",
                column: "Token",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_NhUserAuthRefreshToken_UserId",
                table: "NhUserAuthRefreshToken",
                column: "UserId");

            migrationBuilder.AddForeignKey(
                name: "FK_UserNotificationMessages_NhUserAuthRefreshToken_NhUserAuthRefreshTokenId",
                table: "UserNotificationMessages",
                column: "NhUserAuthRefreshTokenId",
                principalTable: "NhUserAuthRefreshToken",
                principalColumn: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_UserNotificationMessages_NhUserAuthRefreshToken_NhUserAuthRefreshTokenId",
                table: "UserNotificationMessages");

            migrationBuilder.DropTable(
                name: "NhUserAuthRefreshToken");

            migrationBuilder.DropIndex(
                name: "IX_UserNotificationMessages_NhUserAuthRefreshTokenId",
                table: "UserNotificationMessages");

            migrationBuilder.DropColumn(
                name: "NhUserAuthRefreshTokenId",
                table: "UserNotificationMessages");

            migrationBuilder.AddColumn<string>(
                name: "RefreshToken",
                table: "AspNetUsers",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);
        }
    }
}
