using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NewHeap.Platform.AI.Chat.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class AgentCompositions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "UsesCompositions",
                schema: "nhai",
                table: "AssistantAgent",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "UsesScripts",
                schema: "nhai",
                table: "AssistantAgent",
                type: "bit",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "UsesCompositions",
                schema: "nhai",
                table: "AssistantAgent");

            migrationBuilder.DropColumn(
                name: "UsesScripts",
                schema: "nhai",
                table: "AssistantAgent");
        }
    }
}
