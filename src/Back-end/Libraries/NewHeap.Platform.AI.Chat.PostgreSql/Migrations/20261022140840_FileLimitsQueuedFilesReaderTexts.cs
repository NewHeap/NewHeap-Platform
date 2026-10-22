using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NewHeap.Platform.AI.Chat.PostgreSql.Migrations
{
    /// <inheritdoc />
    public partial class FileLimitsQueuedFilesReaderTexts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "FilesJson",
                schema: "nhai",
                table: "AssistantQueuedInput",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MaxFileBytes",
                schema: "nhai",
                table: "AssistantAgentSkillDefinition",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MaxFilePages",
                schema: "nhai",
                table: "AssistantAgentSkillDefinition",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MaxFileTokensPerTurn",
                schema: "nhai",
                table: "AssistantAgentSkillDefinition",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MaxFilesPerTurn",
                schema: "nhai",
                table: "AssistantAgentSkillDefinition",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MaxFileBytes",
                schema: "nhai",
                table: "AssistantAgentRunConfiguration",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MaxFilePages",
                schema: "nhai",
                table: "AssistantAgentRunConfiguration",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MaxFileTokensPerTurn",
                schema: "nhai",
                table: "AssistantAgentRunConfiguration",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MaxFilesPerTurn",
                schema: "nhai",
                table: "AssistantAgentRunConfiguration",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "AssistantFileReaderText",
                schema: "nhai",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ConversationId = table.Column<Guid>(type: "uuid", nullable: false),
                    FileHash = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    ReaderId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    ProtectedText = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AssistantFileReaderText", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AssistantFileReaderText_AssistantConversation_ConversationId",
                        column: x => x.ConversationId,
                        principalSchema: "nhai",
                        principalTable: "AssistantConversation",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AssistantFileReaderText_ConversationId_FileHash_ReaderId",
                schema: "nhai",
                table: "AssistantFileReaderText",
                columns: new[] { "ConversationId", "FileHash", "ReaderId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AssistantFileReaderText_ExpiresAt",
                schema: "nhai",
                table: "AssistantFileReaderText",
                column: "ExpiresAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AssistantFileReaderText",
                schema: "nhai");

            migrationBuilder.DropColumn(
                name: "FilesJson",
                schema: "nhai",
                table: "AssistantQueuedInput");

            migrationBuilder.DropColumn(
                name: "MaxFileBytes",
                schema: "nhai",
                table: "AssistantAgentSkillDefinition");

            migrationBuilder.DropColumn(
                name: "MaxFilePages",
                schema: "nhai",
                table: "AssistantAgentSkillDefinition");

            migrationBuilder.DropColumn(
                name: "MaxFileTokensPerTurn",
                schema: "nhai",
                table: "AssistantAgentSkillDefinition");

            migrationBuilder.DropColumn(
                name: "MaxFilesPerTurn",
                schema: "nhai",
                table: "AssistantAgentSkillDefinition");

            migrationBuilder.DropColumn(
                name: "MaxFileBytes",
                schema: "nhai",
                table: "AssistantAgentRunConfiguration");

            migrationBuilder.DropColumn(
                name: "MaxFilePages",
                schema: "nhai",
                table: "AssistantAgentRunConfiguration");

            migrationBuilder.DropColumn(
                name: "MaxFileTokensPerTurn",
                schema: "nhai",
                table: "AssistantAgentRunConfiguration");

            migrationBuilder.DropColumn(
                name: "MaxFilesPerTurn",
                schema: "nhai",
                table: "AssistantAgentRunConfiguration");
        }
    }
}
