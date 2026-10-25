using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NewHeap.Platform.AI.Chat.PostgreSql.Migrations
{
    /// <inheritdoc />
    public partial class TriggerEventLookup : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_AssistantQueuedInput_EventKey_TriggerId",
                schema: "nhai",
                table: "AssistantQueuedInput",
                columns: new[] { "EventKey", "TriggerId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AssistantQueuedInput_EventKey_TriggerId",
                schema: "nhai",
                table: "AssistantQueuedInput");
        }
    }
}
