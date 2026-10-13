using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NewHeap.Platform.AI.Chat.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class AgentRuns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "GrantSource",
                schema: "nhai",
                table: "AssistantToolInvocation",
                type: "nvarchar(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "None");

            migrationBuilder.AddColumn<string>(
                name: "Kind",
                schema: "nhai",
                table: "AssistantConversation",
                type: "nvarchar(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "chat");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "EscalatedAt",
                schema: "nhai",
                table: "AssistantApproval",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ExecutedAt",
                schema: "nhai",
                table: "AssistantApproval",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ExecutionAttempts",
                schema: "nhai",
                table: "AssistantApproval",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<Guid>(
                name: "ExecutionOperationId",
                schema: "nhai",
                table: "AssistantApproval",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExecutionResultCode",
                schema: "nhai",
                table: "AssistantApproval",
                type: "nvarchar(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "GrantSource",
                schema: "nhai",
                table: "AssistantApproval",
                type: "nvarchar(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "None");

            migrationBuilder.AddColumn<string>(
                name: "InitiatorActorId",
                schema: "nhai",
                table: "AssistantApproval",
                type: "nvarchar(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Mode",
                schema: "nhai",
                table: "AssistantApproval",
                type: "nvarchar(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "inline");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "NextReminderAt",
                schema: "nhai",
                table: "AssistantApproval",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PolicyId",
                schema: "nhai",
                table: "AssistantApproval",
                type: "nvarchar(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PolicyJson",
                schema: "nhai",
                table: "AssistantApproval",
                type: "nvarchar(4000)",
                maxLength: 4000,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RemindersSent",
                schema: "nhai",
                table: "AssistantApproval",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<Guid>(
                name: "RunId",
                schema: "nhai",
                table: "AssistantApproval",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SubjectVersion",
                schema: "nhai",
                table: "AssistantApproval",
                type: "nvarchar(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "AssistantArtifact",
                schema: "nhai",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ConversationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RunId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ToolInvocationId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Version = table.Column<int>(type: "int", nullable: false),
                    ContentType = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    Sha256 = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Content = table.Column<byte[]>(type: "varbinary(max)", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AssistantArtifact", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AssistantArtifact_AssistantConversation_ConversationId",
                        column: x => x.ConversationId,
                        principalSchema: "nhai",
                        principalTable: "AssistantConversation",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AssistantQueuedInput",
                schema: "nhai",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ConversationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    AuthorActorId = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    TriggerId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    Text = table.Column<string>(type: "nvarchar(max)", maxLength: 8000, nullable: false),
                    ClientMessageId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ConsumedByRunId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ConsumedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AssistantQueuedInput", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AssistantQueuedInput_AssistantConversation_ConversationId",
                        column: x => x.ConversationId,
                        principalSchema: "nhai",
                        principalTable: "AssistantConversation",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AssistantRun",
                schema: "nhai",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ConversationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BackgroundOperationId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    TriggerId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    TriggerEventKey = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    InitiatorActorId = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    AccountableOwnerId = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    Attempt = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    FailureClass = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    FailureCode = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    AgentVersion = table.Column<int>(type: "int", nullable: false),
                    TurnId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ToolCalls = table.Column<int>(type: "int", nullable: false),
                    InputTokens = table.Column<long>(type: "bigint", nullable: false),
                    OutputTokens = table.Column<long>(type: "bigint", nullable: false),
                    ProposalCount = table.Column<int>(type: "int", nullable: false),
                    Summary = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    Shadow = table.Column<bool>(type: "bit", nullable: false),
                    CancelledByActorId = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    EndedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ConcurrencyStamp = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AssistantRun", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AssistantRun_AssistantConversation_ConversationId",
                        column: x => x.ConversationId,
                        principalSchema: "nhai",
                        principalTable: "AssistantConversation",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AssistantSubjectMember",
                schema: "nhai",
                columns: table => new
                {
                    ScopeKey = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    SubjectType = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    SubjectId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ActorId = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    Role = table.Column<int>(type: "int", nullable: false),
                    NamedRolesJson = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    DisplayName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    SnapshotAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AssistantSubjectMember", x => new { x.ScopeKey, x.SubjectType, x.SubjectId, x.ActorId });
                });

            migrationBuilder.CreateTable(
                name: "AssistantThread",
                schema: "nhai",
                columns: table => new
                {
                    ConversationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ScopeKey = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    AgentId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    SubjectType = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    SubjectId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    SubjectDisplayName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    DivisionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    TenantId = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    SubjectVersion = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    Accountability = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    AccountableOwnerId = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    MemoryJson = table.Column<string>(type: "nvarchar(max)", maxLength: 65536, nullable: true),
                    MemorySchemaVersion = table.Column<int>(type: "int", nullable: false),
                    MemoryConcurrencyStamp = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MembersRefreshedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AssistantThread", x => x.ConversationId);
                    table.ForeignKey(
                        name: "FK_AssistantThread_AssistantConversation_ConversationId",
                        column: x => x.ConversationId,
                        principalSchema: "nhai",
                        principalTable: "AssistantConversation",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AssistantApproval_RunId",
                schema: "nhai",
                table: "AssistantApproval",
                column: "RunId");

            migrationBuilder.CreateIndex(
                name: "IX_AssistantApproval_Status_ExpiresAt",
                schema: "nhai",
                table: "AssistantApproval",
                columns: new[] { "Status", "ExpiresAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AssistantApproval_Status_NextReminderAt",
                schema: "nhai",
                table: "AssistantApproval",
                columns: new[] { "Status", "NextReminderAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AssistantArtifact_ConversationId_Name_Version",
                schema: "nhai",
                table: "AssistantArtifact",
                columns: new[] { "ConversationId", "Name", "Version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AssistantQueuedInput_ConversationId_ConsumedAt_CreatedAt",
                schema: "nhai",
                table: "AssistantQueuedInput",
                columns: new[] { "ConversationId", "ConsumedAt", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AssistantRun_BackgroundOperationId",
                schema: "nhai",
                table: "AssistantRun",
                column: "BackgroundOperationId");

            migrationBuilder.CreateIndex(
                name: "IX_AssistantRun_ConversationId_CreatedAt",
                schema: "nhai",
                table: "AssistantRun",
                columns: new[] { "ConversationId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AssistantRun_Status_CreatedAt",
                schema: "nhai",
                table: "AssistantRun",
                columns: new[] { "Status", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AssistantSubjectMember_ActorId_Role",
                schema: "nhai",
                table: "AssistantSubjectMember",
                columns: new[] { "ActorId", "Role" });

            migrationBuilder.CreateIndex(
                name: "IX_AssistantThread_ScopeKey_AgentId_SubjectType_SubjectId",
                schema: "nhai",
                table: "AssistantThread",
                columns: new[] { "ScopeKey", "AgentId", "SubjectType", "SubjectId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AssistantThread_SubjectType_SubjectId",
                schema: "nhai",
                table: "AssistantThread",
                columns: new[] { "SubjectType", "SubjectId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AssistantArtifact",
                schema: "nhai");

            migrationBuilder.DropTable(
                name: "AssistantQueuedInput",
                schema: "nhai");

            migrationBuilder.DropTable(
                name: "AssistantRun",
                schema: "nhai");

            migrationBuilder.DropTable(
                name: "AssistantSubjectMember",
                schema: "nhai");

            migrationBuilder.DropTable(
                name: "AssistantThread",
                schema: "nhai");

            migrationBuilder.DropIndex(
                name: "IX_AssistantApproval_RunId",
                schema: "nhai",
                table: "AssistantApproval");

            migrationBuilder.DropIndex(
                name: "IX_AssistantApproval_Status_ExpiresAt",
                schema: "nhai",
                table: "AssistantApproval");

            migrationBuilder.DropIndex(
                name: "IX_AssistantApproval_Status_NextReminderAt",
                schema: "nhai",
                table: "AssistantApproval");

            migrationBuilder.DropColumn(
                name: "GrantSource",
                schema: "nhai",
                table: "AssistantToolInvocation");

            migrationBuilder.DropColumn(
                name: "Kind",
                schema: "nhai",
                table: "AssistantConversation");

            migrationBuilder.DropColumn(
                name: "EscalatedAt",
                schema: "nhai",
                table: "AssistantApproval");

            migrationBuilder.DropColumn(
                name: "ExecutedAt",
                schema: "nhai",
                table: "AssistantApproval");

            migrationBuilder.DropColumn(
                name: "ExecutionAttempts",
                schema: "nhai",
                table: "AssistantApproval");

            migrationBuilder.DropColumn(
                name: "ExecutionOperationId",
                schema: "nhai",
                table: "AssistantApproval");

            migrationBuilder.DropColumn(
                name: "ExecutionResultCode",
                schema: "nhai",
                table: "AssistantApproval");

            migrationBuilder.DropColumn(
                name: "GrantSource",
                schema: "nhai",
                table: "AssistantApproval");

            migrationBuilder.DropColumn(
                name: "InitiatorActorId",
                schema: "nhai",
                table: "AssistantApproval");

            migrationBuilder.DropColumn(
                name: "Mode",
                schema: "nhai",
                table: "AssistantApproval");

            migrationBuilder.DropColumn(
                name: "NextReminderAt",
                schema: "nhai",
                table: "AssistantApproval");

            migrationBuilder.DropColumn(
                name: "PolicyId",
                schema: "nhai",
                table: "AssistantApproval");

            migrationBuilder.DropColumn(
                name: "PolicyJson",
                schema: "nhai",
                table: "AssistantApproval");

            migrationBuilder.DropColumn(
                name: "RemindersSent",
                schema: "nhai",
                table: "AssistantApproval");

            migrationBuilder.DropColumn(
                name: "RunId",
                schema: "nhai",
                table: "AssistantApproval");

            migrationBuilder.DropColumn(
                name: "SubjectVersion",
                schema: "nhai",
                table: "AssistantApproval");
        }
    }
}
