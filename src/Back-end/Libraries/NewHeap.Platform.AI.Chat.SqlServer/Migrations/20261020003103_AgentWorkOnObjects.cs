using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NewHeap.Platform.AI.Chat.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class AgentWorkOnObjects : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "PerDivision",
                schema: "nhai",
                table: "AssistantAgent",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "RunPolicy",
                schema: "nhai",
                table: "AssistantAgent",
                type: "nvarchar(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ViewPolicy",
                schema: "nhai",
                table: "AssistantAgent",
                type: "nvarchar(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "WorksOnAllSubjects",
                schema: "nhai",
                table: "AssistantAgent",
                type: "bit",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "WorksOnApplication",
                schema: "nhai",
                table: "AssistantAgent",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "WorksOnSubjectTypesJson",
                schema: "nhai",
                table: "AssistantAgent",
                type: "nvarchar(2100)",
                maxLength: 2100,
                nullable: false,
                defaultValue: "[]");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PerDivision",
                schema: "nhai",
                table: "AssistantAgent");

            migrationBuilder.DropColumn(
                name: "RunPolicy",
                schema: "nhai",
                table: "AssistantAgent");

            migrationBuilder.DropColumn(
                name: "ViewPolicy",
                schema: "nhai",
                table: "AssistantAgent");

            migrationBuilder.DropColumn(
                name: "WorksOnAllSubjects",
                schema: "nhai",
                table: "AssistantAgent");

            migrationBuilder.DropColumn(
                name: "WorksOnApplication",
                schema: "nhai",
                table: "AssistantAgent");

            migrationBuilder.DropColumn(
                name: "WorksOnSubjectTypesJson",
                schema: "nhai",
                table: "AssistantAgent");
        }
    }
}
