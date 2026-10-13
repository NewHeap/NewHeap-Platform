using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SampleProjectManagement.DAL.Migrations
{
    /// <inheritdoc />
    public partial class AddProjectMembersAndAgentOwnedOperations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<Guid>(
                name: "OwnerUserId",
                table: "BackgroundOperations",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddColumn<string>(
                name: "OwnerKey",
                table: "BackgroundOperations",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "OwnerKind",
                table: "BackgroundOperations",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "ProjectMembers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CreationDateTime = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastModifiedDateTime = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Role = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProjectMembers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProjectMembers_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ProjectMembers_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BackgroundOperations_ConcurrencyKey_Status",
                table: "BackgroundOperations",
                columns: new[] { "ConcurrencyKey", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_BackgroundOperations_DomainObjectType_DomainObjectId_Status",
                table: "BackgroundOperations",
                columns: new[] { "DomainObjectType", "DomainObjectId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_BackgroundOperations_OwnerKind_OwnerKey_Status_LastModified~",
                table: "BackgroundOperations",
                columns: new[] { "OwnerKind", "OwnerKey", "Status", "LastModifiedDateTime" });

            migrationBuilder.CreateIndex(
                name: "IX_ProjectMembers_ProjectId_UserId",
                table: "ProjectMembers",
                columns: new[] { "ProjectId", "UserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProjectMembers_UserId",
                table: "ProjectMembers",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ProjectMembers");

            migrationBuilder.DropIndex(
                name: "IX_BackgroundOperations_ConcurrencyKey_Status",
                table: "BackgroundOperations");

            migrationBuilder.DropIndex(
                name: "IX_BackgroundOperations_DomainObjectType_DomainObjectId_Status",
                table: "BackgroundOperations");

            migrationBuilder.DropIndex(
                name: "IX_BackgroundOperations_OwnerKind_OwnerKey_Status_LastModified~",
                table: "BackgroundOperations");

            migrationBuilder.DropColumn(
                name: "OwnerKey",
                table: "BackgroundOperations");

            migrationBuilder.DropColumn(
                name: "OwnerKind",
                table: "BackgroundOperations");

            migrationBuilder.AlterColumn<Guid>(
                name: "OwnerUserId",
                table: "BackgroundOperations",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);
        }
    }
}
