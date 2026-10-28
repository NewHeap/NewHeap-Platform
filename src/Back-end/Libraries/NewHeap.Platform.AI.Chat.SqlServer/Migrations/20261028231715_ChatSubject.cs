using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NewHeap.Platform.AI.Chat.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class ChatSubject : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SubjectDisplayName",
                schema: "nhai",
                table: "AssistantConversation",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SubjectId",
                schema: "nhai",
                table: "AssistantConversation",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SubjectScopeKey",
                schema: "nhai",
                table: "AssistantConversation",
                type: "nvarchar(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SubjectType",
                schema: "nhai",
                table: "AssistantConversation",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_AssistantConversation_SubjectType_SubjectId",
                schema: "nhai",
                table: "AssistantConversation",
                columns: new[] { "SubjectType", "SubjectId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AssistantConversation_SubjectType_SubjectId",
                schema: "nhai",
                table: "AssistantConversation");

            migrationBuilder.DropColumn(
                name: "SubjectDisplayName",
                schema: "nhai",
                table: "AssistantConversation");

            migrationBuilder.DropColumn(
                name: "SubjectId",
                schema: "nhai",
                table: "AssistantConversation");

            migrationBuilder.DropColumn(
                name: "SubjectScopeKey",
                schema: "nhai",
                table: "AssistantConversation");

            migrationBuilder.DropColumn(
                name: "SubjectType",
                schema: "nhai",
                table: "AssistantConversation");
        }
    }
}
