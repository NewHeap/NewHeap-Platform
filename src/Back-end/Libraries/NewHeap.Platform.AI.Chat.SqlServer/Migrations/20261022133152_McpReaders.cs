using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NewHeap.Platform.AI.Chat.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class McpReaders : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AssistantMcpReader",
                schema: "nhai",
                columns: table => new
                {
                    ServerId = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    ToolName = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    MediaTypes = table.Column<string>(type: "nvarchar(2064)", maxLength: 2064, nullable: false),
                    MaxClassification = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Priority = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AssistantMcpReader", x => new { x.ServerId, x.ToolName });
                    table.ForeignKey(
                        name: "FK_AssistantMcpReader_AssistantMcpServer_ServerId",
                        column: x => x.ServerId,
                        principalSchema: "nhai",
                        principalTable: "AssistantMcpServer",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            // The reader setting of a server becomes its first reader, at priority 0. {nhai} is the
            // configured schema (NhAssistantMigrationSchema.SchemaPlaceholder).
            migrationBuilder.Sql(
                """
                INSERT INTO {nhai}.[AssistantMcpReader] ([ServerId], [ToolName], [MediaTypes], [MaxClassification], [Priority])
                SELECT [Id], [ReaderToolName], [ReaderMediaTypes], [ReaderMaxClassification], 0
                FROM {nhai}.[AssistantMcpServer]
                WHERE [ReaderToolName] IS NOT NULL AND [ReaderMediaTypes] IS NOT NULL AND [ReaderMaxClassification] IS NOT NULL;
                """);

            migrationBuilder.DropColumn(
                name: "ReaderMaxClassification",
                schema: "nhai",
                table: "AssistantMcpServer");

            migrationBuilder.DropColumn(
                name: "ReaderMediaTypes",
                schema: "nhai",
                table: "AssistantMcpServer");

            migrationBuilder.DropColumn(
                name: "ReaderToolName",
                schema: "nhai",
                table: "AssistantMcpServer");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ReaderMaxClassification",
                schema: "nhai",
                table: "AssistantMcpServer",
                type: "nvarchar(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReaderMediaTypes",
                schema: "nhai",
                table: "AssistantMcpServer",
                type: "nvarchar(2064)",
                maxLength: 2064,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReaderToolName",
                schema: "nhai",
                table: "AssistantMcpServer",
                type: "nvarchar(128)",
                maxLength: 128,
                nullable: true);

            // The earlier shape holds one reader per server: the one the assistant asked first.
            migrationBuilder.Sql(
                """
                UPDATE [target]
                SET [ReaderToolName] = [first].[ToolName],
                    [ReaderMediaTypes] = [first].[MediaTypes],
                    [ReaderMaxClassification] = [first].[MaxClassification]
                FROM {nhai}.[AssistantMcpServer] AS [target]
                CROSS APPLY (
                    SELECT TOP (1) [reader].[ToolName], [reader].[MediaTypes], [reader].[MaxClassification]
                    FROM {nhai}.[AssistantMcpReader] AS [reader]
                    WHERE [reader].[ServerId] = [target].[Id]
                    ORDER BY [reader].[Priority] DESC, [reader].[ToolName]
                ) AS [first];
                """);

            migrationBuilder.DropTable(
                name: "AssistantMcpReader",
                schema: "nhai");
        }
    }
}
