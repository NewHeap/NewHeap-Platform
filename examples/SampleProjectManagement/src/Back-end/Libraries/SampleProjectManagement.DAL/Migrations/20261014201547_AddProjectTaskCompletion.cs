using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SampleProjectManagement.DAL.Migrations
{
    /// <inheritdoc />
    public partial class AddProjectTaskCompletion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "CompletedAt",
                table: "ProjectTasks",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "CompletedByUserId",
                table: "ProjectTasks",
                type: "uuid",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CompletedAt",
                table: "ProjectTasks");

            migrationBuilder.DropColumn(
                name: "CompletedByUserId",
                table: "ProjectTasks");
        }
    }
}
