using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SampleProjectManagement.DAL.Migrations
{
    /// <inheritdoc />
    public partial class AddNhBackgroundOperations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "BackgroundOperations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CreationDateTime = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastModifiedDateTime = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    OperationType = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    PayloadSchemaVersion = table.Column<int>(type: "integer", nullable: false),
                    PayloadJson = table.Column<string>(type: "text", nullable: false),
                    OwnerUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    DivisionId = table.Column<Guid>(type: "uuid", nullable: true),
                    ParentOperationId = table.Column<Guid>(type: "uuid", nullable: true),
                    RootOperationId = table.Column<Guid>(type: "uuid", nullable: true),
                    FanOutKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    FanOutItemKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    ProcessorKey = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Queue = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Priority = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    DispatchGeneration = table.Column<int>(type: "integer", nullable: false),
                    SchedulerJobId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    CurrentAttemptNumber = table.Column<int>(type: "integer", nullable: false),
                    CurrentAttemptId = table.Column<Guid>(type: "uuid", nullable: true),
                    ConcurrencyKey = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: true),
                    DomainObjectType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    DomainObjectId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    CorrelationId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    ProgressCurrent = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: true),
                    ProgressTotal = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: true),
                    ProgressPercentage = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: true),
                    ProgressPhaseKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    ProgressMessageKey = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    ProgressMessageArgumentsJson = table.Column<string>(type: "text", nullable: true),
                    CancelRequestedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CancelRequestedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    HeartbeatAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    NextDispatchAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    SensitiveDataRedactedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ResultReferenceType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    ResultReferenceId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    ResultUrl = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    FailureCode = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    FailureMessageKey = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    DiagnosticCorrelationId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    LatestEventSequence = table.Column<long>(type: "bigint", nullable: false),
                    UserNotificationId = table.Column<Guid>(type: "uuid", nullable: true),
                    LastProjectedNotificationEventSequence = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BackgroundOperations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BackgroundOperations_AspNetUsers_OwnerUserId",
                        column: x => x.OwnerUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_BackgroundOperations_BackgroundOperations_ParentOperationId",
                        column: x => x.ParentOperationId,
                        principalTable: "BackgroundOperations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_BackgroundOperations_Divisions_DivisionId",
                        column: x => x.DivisionId,
                        principalTable: "Divisions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_BackgroundOperations_UserNotifications_UserNotificationId",
                        column: x => x.UserNotificationId,
                        principalTable: "UserNotifications",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "BackgroundOperationAttempts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CreationDateTime = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastModifiedDateTime = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    OperationId = table.Column<Guid>(type: "uuid", nullable: false),
                    AttemptNumber = table.Column<int>(type: "integer", nullable: false),
                    DispatchGeneration = table.Column<int>(type: "integer", nullable: false),
                    SchedulerJobId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    WorkerId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    HeartbeatAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    FailureCode = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    DiagnosticCorrelationId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    RecoveryReason = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    Version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BackgroundOperationAttempts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BackgroundOperationAttempts_BackgroundOperations_OperationId",
                        column: x => x.OperationId,
                        principalTable: "BackgroundOperations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "BackgroundOperationCheckpoints",
                columns: table => new
                {
                    OperationId = table.Column<Guid>(type: "uuid", nullable: false),
                    CheckpointKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    SchemaVersion = table.Column<int>(type: "integer", nullable: false),
                    ValueJson = table.Column<string>(type: "text", nullable: false),
                    AttemptId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreationDateTime = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastModifiedDateTime = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BackgroundOperationCheckpoints", x => new { x.OperationId, x.CheckpointKey });
                    table.ForeignKey(
                        name: "FK_BackgroundOperationCheckpoints_BackgroundOperations_Operati~",
                        column: x => x.OperationId,
                        principalTable: "BackgroundOperations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "BackgroundOperationEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OperationId = table.Column<Guid>(type: "uuid", nullable: false),
                    Sequence = table.Column<long>(type: "bigint", nullable: false),
                    StepId = table.Column<Guid>(type: "uuid", nullable: true),
                    StepKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    EventType = table.Column<int>(type: "integer", nullable: false),
                    Severity = table.Column<int>(type: "integer", nullable: false),
                    MessageKey = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    MessageArgumentsJson = table.Column<string>(type: "text", nullable: true),
                    SnapshotVersion = table.Column<long>(type: "bigint", nullable: false),
                    CreationDateTime = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastModifiedDateTime = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ResultReferenceType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    ResultReferenceId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    ResultUrl = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    AttemptId = table.Column<Guid>(type: "uuid", nullable: true),
                    IsMilestone = table.Column<bool>(type: "boolean", nullable: false),
                    IsOperatorOnly = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BackgroundOperationEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BackgroundOperationEvents_BackgroundOperations_OperationId",
                        column: x => x.OperationId,
                        principalTable: "BackgroundOperations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "BackgroundOperationIdempotencyRecords",
                columns: table => new
                {
                    Scope = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    KeyHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    OperationId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreationDateTime = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BackgroundOperationIdempotencyRecords", x => new { x.Scope, x.KeyHash });
                    table.ForeignKey(
                        name: "FK_BackgroundOperationIdempotencyRecords_BackgroundOperations_~",
                        column: x => x.OperationId,
                        principalTable: "BackgroundOperations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "BackgroundOperationLeases",
                columns: table => new
                {
                    ResourceKey = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: false),
                    Slot = table.Column<int>(type: "integer", nullable: false),
                    OperationId = table.Column<Guid>(type: "uuid", nullable: true),
                    AttemptId = table.Column<Guid>(type: "uuid", nullable: true),
                    AcquiredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    HeartbeatAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    FencingToken = table.Column<long>(type: "bigint", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BackgroundOperationLeases", x => new { x.ResourceKey, x.Slot });
                    table.ForeignKey(
                        name: "FK_BackgroundOperationLeases_BackgroundOperations_OperationId",
                        column: x => x.OperationId,
                        principalTable: "BackgroundOperations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "BackgroundOperationSteps",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CreationDateTime = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastModifiedDateTime = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    OperationId = table.Column<Guid>(type: "uuid", nullable: false),
                    ParentStepId = table.Column<Guid>(type: "uuid", nullable: true),
                    StepKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    TitleKey = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    TitleArgumentsJson = table.Column<string>(type: "text", nullable: true),
                    MessageKey = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    MessageArgumentsJson = table.Column<string>(type: "text", nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    AggregationMode = table.Column<int>(type: "integer", nullable: false),
                    Weight = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    Current = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: true),
                    Total = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: true),
                    Percentage = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: true),
                    DiscoveredItems = table.Column<long>(type: "bigint", nullable: false),
                    ProcessedItems = table.Column<long>(type: "bigint", nullable: false),
                    SucceededItems = table.Column<long>(type: "bigint", nullable: false),
                    FailedItems = table.Column<long>(type: "bigint", nullable: false),
                    SkippedItems = table.Column<long>(type: "bigint", nullable: false),
                    RetriedItems = table.Column<long>(type: "bigint", nullable: false),
                    ActiveItems = table.Column<long>(type: "bigint", nullable: false),
                    ContinueOnChildFailure = table.Column<bool>(type: "boolean", nullable: false),
                    DisplayOrder = table.Column<int>(type: "integer", nullable: false),
                    Depth = table.Column<int>(type: "integer", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    HeartbeatAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CurrentAttemptId = table.Column<Guid>(type: "uuid", nullable: true),
                    FencingVersion = table.Column<long>(type: "bigint", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BackgroundOperationSteps", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BackgroundOperationSteps_BackgroundOperationSteps_ParentSte~",
                        column: x => x.ParentStepId,
                        principalTable: "BackgroundOperationSteps",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_BackgroundOperationSteps_BackgroundOperations_OperationId",
                        column: x => x.OperationId,
                        principalTable: "BackgroundOperations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BackgroundOperationAttempts_OperationId_AttemptNumber",
                table: "BackgroundOperationAttempts",
                columns: new[] { "OperationId", "AttemptNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BackgroundOperationAttempts_OperationId_StartedAt",
                table: "BackgroundOperationAttempts",
                columns: new[] { "OperationId", "StartedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_BackgroundOperationEvents_OperationId_CreationDateTime",
                table: "BackgroundOperationEvents",
                columns: new[] { "OperationId", "CreationDateTime" });

            migrationBuilder.CreateIndex(
                name: "IX_BackgroundOperationEvents_OperationId_Sequence",
                table: "BackgroundOperationEvents",
                columns: new[] { "OperationId", "Sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BackgroundOperationIdempotencyRecords_ExpiresAt",
                table: "BackgroundOperationIdempotencyRecords",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_BackgroundOperationIdempotencyRecords_OperationId",
                table: "BackgroundOperationIdempotencyRecords",
                column: "OperationId");

            migrationBuilder.CreateIndex(
                name: "IX_BackgroundOperationLeases_ExpiresAt",
                table: "BackgroundOperationLeases",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_BackgroundOperationLeases_OperationId",
                table: "BackgroundOperationLeases",
                column: "OperationId");

            migrationBuilder.CreateIndex(
                name: "IX_BackgroundOperations_DivisionId_Status_LastModifiedDateTime",
                table: "BackgroundOperations",
                columns: new[] { "DivisionId", "Status", "LastModifiedDateTime" });

            migrationBuilder.CreateIndex(
                name: "IX_BackgroundOperations_OwnerUserId_Status_LastModifiedDateTime",
                table: "BackgroundOperations",
                columns: new[] { "OwnerUserId", "Status", "LastModifiedDateTime" });

            migrationBuilder.CreateIndex(
                name: "IX_BackgroundOperations_ParentOperationId",
                table: "BackgroundOperations",
                column: "ParentOperationId");

            migrationBuilder.CreateIndex(
                name: "IX_BackgroundOperations_ParentOperationId_FanOutKey_FanOutItem~",
                table: "BackgroundOperations",
                columns: new[] { "ParentOperationId", "FanOutKey", "FanOutItemKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BackgroundOperations_ProcessorKey_Status_NextDispatchAt_Pri~",
                table: "BackgroundOperations",
                columns: new[] { "ProcessorKey", "Status", "NextDispatchAt", "Priority", "CreationDateTime" });

            migrationBuilder.CreateIndex(
                name: "IX_BackgroundOperations_RootOperationId",
                table: "BackgroundOperations",
                column: "RootOperationId");

            migrationBuilder.CreateIndex(
                name: "IX_BackgroundOperations_SchedulerJobId",
                table: "BackgroundOperations",
                column: "SchedulerJobId");

            migrationBuilder.CreateIndex(
                name: "IX_BackgroundOperations_Status_CompletedAt",
                table: "BackgroundOperations",
                columns: new[] { "Status", "CompletedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_BackgroundOperations_Status_HeartbeatAt",
                table: "BackgroundOperations",
                columns: new[] { "Status", "HeartbeatAt" });

            migrationBuilder.CreateIndex(
                name: "IX_BackgroundOperations_UserNotificationId",
                table: "BackgroundOperations",
                column: "UserNotificationId");

            migrationBuilder.CreateIndex(
                name: "IX_BackgroundOperationSteps_OperationId_DisplayOrder_Status",
                table: "BackgroundOperationSteps",
                columns: new[] { "OperationId", "DisplayOrder", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_BackgroundOperationSteps_OperationId_ParentStepId_StepKey",
                table: "BackgroundOperationSteps",
                columns: new[] { "OperationId", "ParentStepId", "StepKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BackgroundOperationSteps_ParentStepId",
                table: "BackgroundOperationSteps",
                column: "ParentStepId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BackgroundOperationAttempts");

            migrationBuilder.DropTable(
                name: "BackgroundOperationCheckpoints");

            migrationBuilder.DropTable(
                name: "BackgroundOperationEvents");

            migrationBuilder.DropTable(
                name: "BackgroundOperationIdempotencyRecords");

            migrationBuilder.DropTable(
                name: "BackgroundOperationLeases");

            migrationBuilder.DropTable(
                name: "BackgroundOperationSteps");

            migrationBuilder.DropTable(
                name: "BackgroundOperations");
        }
    }
}
