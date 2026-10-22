using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NewHeap.Platform.AI.Chat.SqlServer.Migrations
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
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MaxFileBytes",
                schema: "nhai",
                table: "AssistantAgentSkillDefinition",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MaxFilePages",
                schema: "nhai",
                table: "AssistantAgentSkillDefinition",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MaxFileTokensPerTurn",
                schema: "nhai",
                table: "AssistantAgentSkillDefinition",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MaxFilesPerTurn",
                schema: "nhai",
                table: "AssistantAgentSkillDefinition",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MaxFileBytes",
                schema: "nhai",
                table: "AssistantAgentRunConfiguration",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MaxFilePages",
                schema: "nhai",
                table: "AssistantAgentRunConfiguration",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MaxFileTokensPerTurn",
                schema: "nhai",
                table: "AssistantAgentRunConfiguration",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MaxFilesPerTurn",
                schema: "nhai",
                table: "AssistantAgentRunConfiguration",
                type: "int",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "AssistantFileReaderText",
                schema: "nhai",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ConversationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FileHash = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    ReaderId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    ProtectedText = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
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
