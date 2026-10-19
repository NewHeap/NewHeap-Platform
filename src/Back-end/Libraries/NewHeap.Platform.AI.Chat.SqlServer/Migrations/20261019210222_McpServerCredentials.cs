using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NewHeap.Platform.AI.Chat.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class McpServerCredentials : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ClientCertificateReference",
                schema: "nhai",
                table: "AssistantMcpServer",
                type: "nvarchar(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SecretReference",
                schema: "nhai",
                table: "AssistantMcpServer",
                type: "nvarchar(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Transport",
                schema: "nhai",
                table: "AssistantMcpServer",
                type: "nvarchar(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "streamable-http");

            migrationBuilder.AddColumn<string>(
                name: "UserName",
                schema: "nhai",
                table: "AssistantMcpServer",
                type: "nvarchar(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "AssistantMcpConnection",
                schema: "nhai",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ServerId = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    OwnerKind = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    OwnerActorId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    TenantKey = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    AllowBackgroundUse = table.Column<bool>(type: "bit", nullable: false),
                    ConsentHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    ConsentedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Version = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AssistantMcpConnection", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AssistantMcpConnection_AssistantMcpServer_ServerId",
                        column: x => x.ServerId,
                        principalSchema: "nhai",
                        principalTable: "AssistantMcpServer",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AssistantMcpConnection_OwnerActorId_OwnerKind",
                schema: "nhai",
                table: "AssistantMcpConnection",
                columns: new[] { "OwnerActorId", "OwnerKind" });

            migrationBuilder.CreateIndex(
                name: "IX_AssistantMcpConnection_ServerId_OwnerKind_OwnerActorId_TenantKey",
                schema: "nhai",
                table: "AssistantMcpConnection",
                columns: new[] { "ServerId", "OwnerKind", "OwnerActorId", "TenantKey" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AssistantMcpConnection",
                schema: "nhai");

            migrationBuilder.DropColumn(
                name: "ClientCertificateReference",
                schema: "nhai",
                table: "AssistantMcpServer");

            migrationBuilder.DropColumn(
                name: "SecretReference",
                schema: "nhai",
                table: "AssistantMcpServer");

            migrationBuilder.DropColumn(
                name: "Transport",
                schema: "nhai",
                table: "AssistantMcpServer");

            migrationBuilder.DropColumn(
                name: "UserName",
                schema: "nhai",
                table: "AssistantMcpServer");
        }
    }
}
