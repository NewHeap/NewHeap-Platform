using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NewHeap.Platform.AI.Chat.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class Collaboration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AuthorActorId",
                schema: "nhai",
                table: "AssistantMessage",
                type: "nvarchar(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ActiveActorId",
                schema: "nhai",
                table: "AssistantConversation",
                type: "nvarchar(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OwnerDisplayName",
                schema: "nhai",
                table: "AssistantConversation",
                type: "nvarchar(120)",
                maxLength: 120,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "OwnerLastReadSequence",
                schema: "nhai",
                table: "AssistantConversation",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProtectedShareToken",
                schema: "nhai",
                table: "AssistantConversation",
                type: "nvarchar(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "AssistantConversationParticipant",
                schema: "nhai",
                columns: table => new
                {
                    ConversationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ActorId = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    DisplayName = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    JoinedVia = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    InvitedByActorId = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    JoinedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    LastReadSequence = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AssistantConversationParticipant", x => new { x.ConversationId, x.ActorId });
                    table.ForeignKey(
                        name: "FK_AssistantConversationParticipant_AssistantConversation_ConversationId",
                        column: x => x.ConversationId,
                        principalSchema: "nhai",
                        principalTable: "AssistantConversation",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AssistantNotificationSetting",
                schema: "nhai",
                columns: table => new
                {
                    ActorId = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    PushEnabled = table.Column<bool>(type: "bit", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AssistantNotificationSetting", x => x.ActorId);
                });

            migrationBuilder.CreateTable(
                name: "AssistantPushSubscription",
                schema: "nhai",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ActorId = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    EndpointHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Endpoint = table.Column<string>(type: "nvarchar(2048)", maxLength: 2048, nullable: false),
                    P256dh = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    Auth = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Language = table.Column<string>(type: "nvarchar(8)", maxLength: 8, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AssistantPushSubscription", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AssistantConversationParticipant_ActorId",
                schema: "nhai",
                table: "AssistantConversationParticipant",
                column: "ActorId");

            migrationBuilder.CreateIndex(
                name: "IX_AssistantPushSubscription_ActorId",
                schema: "nhai",
                table: "AssistantPushSubscription",
                column: "ActorId");

            migrationBuilder.CreateIndex(
                name: "IX_AssistantPushSubscription_EndpointHash",
                schema: "nhai",
                table: "AssistantPushSubscription",
                column: "EndpointHash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AssistantConversationParticipant",
                schema: "nhai");

            migrationBuilder.DropTable(
                name: "AssistantNotificationSetting",
                schema: "nhai");

            migrationBuilder.DropTable(
                name: "AssistantPushSubscription",
                schema: "nhai");

            migrationBuilder.DropColumn(
                name: "AuthorActorId",
                schema: "nhai",
                table: "AssistantMessage");

            migrationBuilder.DropColumn(
                name: "ActiveActorId",
                schema: "nhai",
                table: "AssistantConversation");

            migrationBuilder.DropColumn(
                name: "OwnerDisplayName",
                schema: "nhai",
                table: "AssistantConversation");

            migrationBuilder.DropColumn(
                name: "OwnerLastReadSequence",
                schema: "nhai",
                table: "AssistantConversation");

            migrationBuilder.DropColumn(
                name: "ProtectedShareToken",
                schema: "nhai",
                table: "AssistantConversation");
        }
    }
}
