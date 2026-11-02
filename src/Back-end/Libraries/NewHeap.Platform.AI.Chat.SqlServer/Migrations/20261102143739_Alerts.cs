using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NewHeap.Platform.AI.Chat.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class Alerts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AssistantAlert",
                schema: "nhai",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    KindId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    ScopeKey = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    SubjectType = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    SubjectId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    SubjectDisplayName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    DivisionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    TenantId = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    ParentSubjectType = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    ParentSubjectId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    ParentDisplayName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Key = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    IdentityHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    ClosedMarker = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Urgency = table.Column<int>(type: "int", nullable: false),
                    ParametersJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Revision = table.Column<int>(type: "int", nullable: false),
                    Shadow = table.Column<bool>(type: "bit", nullable: false),
                    AgentId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    RunId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RaisedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    DeliveryPlannedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ClosedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ConcurrencyStamp = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AssistantAlert", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AssistantAlertDelivery",
                schema: "nhai",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AlertId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RecipientActorId = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    ReasonCode = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    DueAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ClaimedBy = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    ClaimedUntil = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    DigestId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Channels = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    Attempts = table.Column<int>(type: "int", nullable: false),
                    PlannedRevision = table.Column<int>(type: "int", nullable: false),
                    SentRevision = table.Column<int>(type: "int", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    SentAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ReadAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    DismissedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ClosedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ConcurrencyStamp = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AssistantAlertDelivery", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AssistantAlertDelivery_AssistantAlert_AlertId",
                        column: x => x.AlertId,
                        principalSchema: "nhai",
                        principalTable: "AssistantAlert",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AssistantAlert_IdentityHash_ClosedMarker",
                schema: "nhai",
                table: "AssistantAlert",
                columns: new[] { "IdentityHash", "ClosedMarker" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AssistantAlert_Status_ClosedAt",
                schema: "nhai",
                table: "AssistantAlert",
                columns: new[] { "Status", "ClosedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AssistantAlert_Status_ExpiresAt",
                schema: "nhai",
                table: "AssistantAlert",
                columns: new[] { "Status", "ExpiresAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AssistantAlert_SubjectType_SubjectId_RaisedAt",
                schema: "nhai",
                table: "AssistantAlert",
                columns: new[] { "SubjectType", "SubjectId", "RaisedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AssistantAlertDelivery_AlertId_RecipientActorId",
                schema: "nhai",
                table: "AssistantAlertDelivery",
                columns: new[] { "AlertId", "RecipientActorId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AssistantAlertDelivery_RecipientActorId_Status",
                schema: "nhai",
                table: "AssistantAlertDelivery",
                columns: new[] { "RecipientActorId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_AssistantAlertDelivery_Status_DueAt",
                schema: "nhai",
                table: "AssistantAlertDelivery",
                columns: new[] { "Status", "DueAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AssistantAlertDelivery",
                schema: "nhai");

            migrationBuilder.DropTable(
                name: "AssistantAlert",
                schema: "nhai");
        }
    }
}
