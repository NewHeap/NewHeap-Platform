using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NewHeap.Platform.AI.Chat.PostgreSql.Migrations
{
    /// <inheritdoc />
    public partial class MetadataIndexValues : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AssistantAgentMetadataIndexValue",
                schema: "nhai",
                columns: table => new
                {
                    AgentId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    IndexValue = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AssistantAgentMetadataIndexValue", x => new { x.AgentId, x.Name, x.IndexValue });
                    table.ForeignKey(
                        name: "FK_AssistantAgentMetadataIndexValue_AssistantAgentMetadata_Age~",
                        columns: x => new { x.AgentId, x.Name },
                        principalSchema: "nhai",
                        principalTable: "AssistantAgentMetadata",
                        principalColumns: new[] { "AgentId", "Name" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AssistantAgentMetadataIndexValue_Name_IndexValue",
                schema: "nhai",
                table: "AssistantAgentMetadataIndexValue",
                columns: new[] { "Name", "IndexValue" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AssistantAgentMetadataIndexValue",
                schema: "nhai");
        }
    }
}
