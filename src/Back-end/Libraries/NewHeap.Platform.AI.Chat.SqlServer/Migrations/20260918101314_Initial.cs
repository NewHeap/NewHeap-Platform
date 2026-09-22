using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NewHeap.Platform.AI.Chat.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "nhai");

            migrationBuilder.CreateTable(
                name: "AssistantBudgetLedger",
                schema: "nhai",
                columns: table => new
                {
                    ActorId = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    Day = table.Column<DateOnly>(type: "date", nullable: false),
                    ToolCalls = table.Column<int>(type: "int", nullable: false),
                    ModelCalls = table.Column<int>(type: "int", nullable: false),
                    InputTokens = table.Column<long>(type: "bigint", nullable: false),
                    OutputTokens = table.Column<long>(type: "bigint", nullable: false),
                    EstimatedCost = table.Column<decimal>(type: "decimal(18,6)", precision: 18, scale: 6, nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AssistantBudgetLedger", x => new { x.ActorId, x.Day });
                });

            migrationBuilder.CreateTable(
                name: "AssistantConversation",
                schema: "nhai",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OwnerActorId = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    TenantId = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    AgentId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    AgentVersion = table.Column<int>(type: "int", nullable: false),
                    Title = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Status = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    ActiveTurnId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ConcurrencyStamp = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AssistantConversation", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AssistantIdempotencyLease",
                schema: "nhai",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    KeyHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    ActorId = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    ToolId = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    ToolVersion = table.Column<int>(type: "int", nullable: false),
                    IdempotencyKey = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    ArgumentHash = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    FencingToken = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    LeaseId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Generation = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Outcome = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    AcquiredAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AssistantIdempotencyLease", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AssistantApproval",
                schema: "nhai",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ConversationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TurnId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ToolInvocationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProposalId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProposalHash = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    ProposalJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ToolId = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    Summary = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false),
                    ArgumentsPreview = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    TargetsJson = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    DecidedByActorId = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    DecidedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ApprovalExpiresAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    Reason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    ConcurrencyStamp = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AssistantApproval", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AssistantApproval_AssistantConversation_ConversationId",
                        column: x => x.ConversationId,
                        principalSchema: "nhai",
                        principalTable: "AssistantConversation",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AssistantMessage",
                schema: "nhai",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ConversationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TurnId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Role = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Sequence = table.Column<int>(type: "int", nullable: false),
                    PartsVersion = table.Column<int>(type: "int", nullable: false),
                    PartsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ClientMessageId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    InputTokens = table.Column<int>(type: "int", nullable: false),
                    OutputTokens = table.Column<int>(type: "int", nullable: false),
                    ToolCalls = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AssistantMessage", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AssistantMessage_AssistantConversation_ConversationId",
                        column: x => x.ConversationId,
                        principalSchema: "nhai",
                        principalTable: "AssistantConversation",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AssistantToolInvocation",
                schema: "nhai",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ConversationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TurnId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MessageId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CallId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    FunctionName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    ToolId = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    ToolVersion = table.Column<int>(type: "int", nullable: false),
                    ContractHash = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    DisplayName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    ResultCode = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    ArgumentsJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ResultJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    DataClassification = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    RetentionCategory = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    ProposalId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ApprovalId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    IdempotencyKey = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    StartedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AssistantToolInvocation", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AssistantToolInvocation_AssistantConversation_ConversationId",
                        column: x => x.ConversationId,
                        principalSchema: "nhai",
                        principalTable: "AssistantConversation",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AssistantApproval_ConversationId",
                schema: "nhai",
                table: "AssistantApproval",
                column: "ConversationId");

            migrationBuilder.CreateIndex(
                name: "IX_AssistantApproval_ProposalHash",
                schema: "nhai",
                table: "AssistantApproval",
                column: "ProposalHash");

            migrationBuilder.CreateIndex(
                name: "IX_AssistantConversation_OwnerActorId_UpdatedAt",
                schema: "nhai",
                table: "AssistantConversation",
                columns: new[] { "OwnerActorId", "UpdatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AssistantIdempotencyLease_KeyHash",
                schema: "nhai",
                table: "AssistantIdempotencyLease",
                column: "KeyHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AssistantMessage_ConversationId_CreatedAt",
                schema: "nhai",
                table: "AssistantMessage",
                columns: new[] { "ConversationId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AssistantToolInvocation_ConversationId_StartedAt",
                schema: "nhai",
                table: "AssistantToolInvocation",
                columns: new[] { "ConversationId", "StartedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AssistantApproval",
                schema: "nhai");

            migrationBuilder.DropTable(
                name: "AssistantBudgetLedger",
                schema: "nhai");

            migrationBuilder.DropTable(
                name: "AssistantIdempotencyLease",
                schema: "nhai");

            migrationBuilder.DropTable(
                name: "AssistantMessage",
                schema: "nhai");

            migrationBuilder.DropTable(
                name: "AssistantToolInvocation",
                schema: "nhai");

            migrationBuilder.DropTable(
                name: "AssistantConversation",
                schema: "nhai");
        }
    }
}
