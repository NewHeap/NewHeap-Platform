using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NewHeap.Platform.AI.Chat.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class McpOAuthConnections : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "AuthorizationConfirmedAt",
                schema: "nhai",
                table: "AssistantMcpServer",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AuthorizationConfirmedBy",
                schema: "nhai",
                table: "AssistantMcpServer",
                type: "nvarchar(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AuthorizationServerIssuer",
                schema: "nhai",
                table: "AssistantMcpServer",
                type: "nvarchar(2048)",
                maxLength: 2048,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ConnectionScope",
                schema: "nhai",
                table: "AssistantMcpServer",
                type: "nvarchar(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "organization");

            migrationBuilder.AddColumn<string>(
                name: "OAuthClientId",
                schema: "nhai",
                table: "AssistantMcpServer",
                type: "nvarchar(512)",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OAuthClientSecretReference",
                schema: "nhai",
                table: "AssistantMcpServer",
                type: "nvarchar(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OAuthScopes",
                schema: "nhai",
                table: "AssistantMcpServer",
                type: "nvarchar(1024)",
                maxLength: 1024,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OrganizationAccountLabel",
                schema: "nhai",
                table: "AssistantMcpServer",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProtectedOAuthClientSecret",
                schema: "nhai",
                table: "AssistantMcpServer",
                type: "nvarchar(4000)",
                maxLength: 4000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ResourceUri",
                schema: "nhai",
                table: "AssistantMcpServer",
                type: "nvarchar(2048)",
                maxLength: 2048,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "AccessTokenExpiresAt",
                schema: "nhai",
                table: "AssistantMcpConnection",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ClientId",
                schema: "nhai",
                table: "AssistantMcpConnection",
                type: "nvarchar(512)",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ConnectedAt",
                schema: "nhai",
                table: "AssistantMcpConnection",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ConnectedBy",
                schema: "nhai",
                table: "AssistantMcpConnection",
                type: "nvarchar(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "GrantedScopes",
                schema: "nhai",
                table: "AssistantMcpConnection",
                type: "nvarchar(1024)",
                maxLength: 1024,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Issuer",
                schema: "nhai",
                table: "AssistantMcpConnection",
                type: "nvarchar(2048)",
                maxLength: 2048,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastFailureCode",
                schema: "nhai",
                table: "AssistantMcpConnection",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastRefreshedAt",
                schema: "nhai",
                table: "AssistantMcpConnection",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastUsedAt",
                schema: "nhai",
                table: "AssistantMcpConnection",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProtectedAccessToken",
                schema: "nhai",
                table: "AssistantMcpConnection",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProtectedRefreshToken",
                schema: "nhai",
                table: "AssistantMcpConnection",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RefreshLeaseOwner",
                schema: "nhai",
                table: "AssistantMcpConnection",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "RefreshLeaseUntil",
                schema: "nhai",
                table: "AssistantMcpConnection",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "RefreshTokenExpiresAt",
                schema: "nhai",
                table: "AssistantMcpConnection",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RequiredScopes",
                schema: "nhai",
                table: "AssistantMcpConnection",
                type: "nvarchar(1024)",
                maxLength: 1024,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Resource",
                schema: "nhai",
                table: "AssistantMcpConnection",
                type: "nvarchar(2048)",
                maxLength: 2048,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Status",
                schema: "nhai",
                table: "AssistantMcpConnection",
                type: "nvarchar(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "AssistantMcpAuthorizationFlow",
                schema: "nhai",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ServerId = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    OwnerKind = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    OwnerActorId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    TenantKey = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    StartedBy = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    StateHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    ProtectedCodeVerifier = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Issuer = table.Column<string>(type: "nvarchar(2048)", maxLength: 2048, nullable: false),
                    Resource = table.Column<string>(type: "nvarchar(2048)", maxLength: 2048, nullable: false),
                    ClientId = table.Column<string>(type: "nvarchar(2048)", maxLength: 2048, nullable: false),
                    RedirectUri = table.Column<string>(type: "nvarchar(2048)", maxLength: 2048, nullable: false),
                    RequestedScopes = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: true),
                    AllowBackgroundUse = table.Column<bool>(type: "bit", nullable: false),
                    ReturnUrl = table.Column<string>(type: "nvarchar(2048)", maxLength: 2048, nullable: true),
                    CompletionCodeHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    ProtectedTokenResponse = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Status = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    FailureCode = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    Version = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AssistantMcpAuthorizationFlow", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AssistantMcpAuthorizationFlow_AssistantMcpServer_ServerId",
                        column: x => x.ServerId,
                        principalSchema: "nhai",
                        principalTable: "AssistantMcpServer",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AssistantMcpClientRegistration",
                schema: "nhai",
                columns: table => new
                {
                    ServerId = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    IssuerHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Issuer = table.Column<string>(type: "nvarchar(2048)", maxLength: 2048, nullable: false),
                    Method = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    ClientId = table.Column<string>(type: "nvarchar(2048)", maxLength: 2048, nullable: false),
                    ProtectedClientSecret = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ProtectedRegistrationAccessToken = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    TokenEndpointAuthMethod = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    ClientSecretExpiresAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    RegisteredAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AssistantMcpClientRegistration", x => new { x.ServerId, x.IssuerHash });
                    table.ForeignKey(
                        name: "FK_AssistantMcpClientRegistration_AssistantMcpServer_ServerId",
                        column: x => x.ServerId,
                        principalSchema: "nhai",
                        principalTable: "AssistantMcpServer",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AssistantMcpAuthorizationFlow_ExpiresAt",
                schema: "nhai",
                table: "AssistantMcpAuthorizationFlow",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_AssistantMcpAuthorizationFlow_ServerId",
                schema: "nhai",
                table: "AssistantMcpAuthorizationFlow",
                column: "ServerId");

            migrationBuilder.CreateIndex(
                name: "IX_AssistantMcpAuthorizationFlow_StartedBy_Status",
                schema: "nhai",
                table: "AssistantMcpAuthorizationFlow",
                columns: new[] { "StartedBy", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_AssistantMcpAuthorizationFlow_StateHash",
                schema: "nhai",
                table: "AssistantMcpAuthorizationFlow",
                column: "StateHash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AssistantMcpAuthorizationFlow",
                schema: "nhai");

            migrationBuilder.DropTable(
                name: "AssistantMcpClientRegistration",
                schema: "nhai");

            migrationBuilder.DropColumn(
                name: "AuthorizationConfirmedAt",
                schema: "nhai",
                table: "AssistantMcpServer");

            migrationBuilder.DropColumn(
                name: "AuthorizationConfirmedBy",
                schema: "nhai",
                table: "AssistantMcpServer");

            migrationBuilder.DropColumn(
                name: "AuthorizationServerIssuer",
                schema: "nhai",
                table: "AssistantMcpServer");

            migrationBuilder.DropColumn(
                name: "ConnectionScope",
                schema: "nhai",
                table: "AssistantMcpServer");

            migrationBuilder.DropColumn(
                name: "OAuthClientId",
                schema: "nhai",
                table: "AssistantMcpServer");

            migrationBuilder.DropColumn(
                name: "OAuthClientSecretReference",
                schema: "nhai",
                table: "AssistantMcpServer");

            migrationBuilder.DropColumn(
                name: "OAuthScopes",
                schema: "nhai",
                table: "AssistantMcpServer");

            migrationBuilder.DropColumn(
                name: "OrganizationAccountLabel",
                schema: "nhai",
                table: "AssistantMcpServer");

            migrationBuilder.DropColumn(
                name: "ProtectedOAuthClientSecret",
                schema: "nhai",
                table: "AssistantMcpServer");

            migrationBuilder.DropColumn(
                name: "ResourceUri",
                schema: "nhai",
                table: "AssistantMcpServer");

            migrationBuilder.DropColumn(
                name: "AccessTokenExpiresAt",
                schema: "nhai",
                table: "AssistantMcpConnection");

            migrationBuilder.DropColumn(
                name: "ClientId",
                schema: "nhai",
                table: "AssistantMcpConnection");

            migrationBuilder.DropColumn(
                name: "ConnectedAt",
                schema: "nhai",
                table: "AssistantMcpConnection");

            migrationBuilder.DropColumn(
                name: "ConnectedBy",
                schema: "nhai",
                table: "AssistantMcpConnection");

            migrationBuilder.DropColumn(
                name: "GrantedScopes",
                schema: "nhai",
                table: "AssistantMcpConnection");

            migrationBuilder.DropColumn(
                name: "Issuer",
                schema: "nhai",
                table: "AssistantMcpConnection");

            migrationBuilder.DropColumn(
                name: "LastFailureCode",
                schema: "nhai",
                table: "AssistantMcpConnection");

            migrationBuilder.DropColumn(
                name: "LastRefreshedAt",
                schema: "nhai",
                table: "AssistantMcpConnection");

            migrationBuilder.DropColumn(
                name: "LastUsedAt",
                schema: "nhai",
                table: "AssistantMcpConnection");

            migrationBuilder.DropColumn(
                name: "ProtectedAccessToken",
                schema: "nhai",
                table: "AssistantMcpConnection");

            migrationBuilder.DropColumn(
                name: "ProtectedRefreshToken",
                schema: "nhai",
                table: "AssistantMcpConnection");

            migrationBuilder.DropColumn(
                name: "RefreshLeaseOwner",
                schema: "nhai",
                table: "AssistantMcpConnection");

            migrationBuilder.DropColumn(
                name: "RefreshLeaseUntil",
                schema: "nhai",
                table: "AssistantMcpConnection");

            migrationBuilder.DropColumn(
                name: "RefreshTokenExpiresAt",
                schema: "nhai",
                table: "AssistantMcpConnection");

            migrationBuilder.DropColumn(
                name: "RequiredScopes",
                schema: "nhai",
                table: "AssistantMcpConnection");

            migrationBuilder.DropColumn(
                name: "Resource",
                schema: "nhai",
                table: "AssistantMcpConnection");

            migrationBuilder.DropColumn(
                name: "Status",
                schema: "nhai",
                table: "AssistantMcpConnection");
        }
    }
}
