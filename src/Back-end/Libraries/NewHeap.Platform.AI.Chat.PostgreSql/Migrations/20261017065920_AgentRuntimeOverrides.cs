using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NewHeap.Platform.AI.Chat.PostgreSql.Migrations
{
    /// <inheritdoc />
    public partial class AgentRuntimeOverrides : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AssistantThread_ScopeKey_AgentId_SubjectType_SubjectId",
                schema: "nhai",
                table: "AssistantThread");

            migrationBuilder.AddColumn<bool>(
                name: "Shadow",
                schema: "nhai",
                table: "AssistantThread",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ShadowChangedAt",
                schema: "nhai",
                table: "AssistantAgentSkillState",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ShadowChangedBy",
                schema: "nhai",
                table: "AssistantAgentSkillState",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "ShadowOverride",
                schema: "nhai",
                table: "AssistantAgentSkillState",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ShadowReason",
                schema: "nhai",
                table: "AssistantAgentSkillState",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ServiceAccountActorId",
                schema: "nhai",
                table: "AssistantAgent",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ServiceAccountChangedAt",
                schema: "nhai",
                table: "AssistantAgent",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ServiceAccountChangedBy",
                schema: "nhai",
                table: "AssistantAgent",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ServiceAccountReason",
                schema: "nhai",
                table: "AssistantAgent",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ShadowChangedAt",
                schema: "nhai",
                table: "AssistantAgent",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ShadowChangedBy",
                schema: "nhai",
                table: "AssistantAgent",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "ShadowOverride",
                schema: "nhai",
                table: "AssistantAgent",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ShadowReason",
                schema: "nhai",
                table: "AssistantAgent",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_AssistantThread_ScopeKey_AgentId_SubjectType_SubjectId_Shad~",
                schema: "nhai",
                table: "AssistantThread",
                columns: new[] { "ScopeKey", "AgentId", "SubjectType", "SubjectId", "Shadow" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AssistantThread_ScopeKey_AgentId_SubjectType_SubjectId_Shad~",
                schema: "nhai",
                table: "AssistantThread");

            migrationBuilder.DropColumn(
                name: "Shadow",
                schema: "nhai",
                table: "AssistantThread");

            migrationBuilder.DropColumn(
                name: "ShadowChangedAt",
                schema: "nhai",
                table: "AssistantAgentSkillState");

            migrationBuilder.DropColumn(
                name: "ShadowChangedBy",
                schema: "nhai",
                table: "AssistantAgentSkillState");

            migrationBuilder.DropColumn(
                name: "ShadowOverride",
                schema: "nhai",
                table: "AssistantAgentSkillState");

            migrationBuilder.DropColumn(
                name: "ShadowReason",
                schema: "nhai",
                table: "AssistantAgentSkillState");

            migrationBuilder.DropColumn(
                name: "ServiceAccountActorId",
                schema: "nhai",
                table: "AssistantAgent");

            migrationBuilder.DropColumn(
                name: "ServiceAccountChangedAt",
                schema: "nhai",
                table: "AssistantAgent");

            migrationBuilder.DropColumn(
                name: "ServiceAccountChangedBy",
                schema: "nhai",
                table: "AssistantAgent");

            migrationBuilder.DropColumn(
                name: "ServiceAccountReason",
                schema: "nhai",
                table: "AssistantAgent");

            migrationBuilder.DropColumn(
                name: "ShadowChangedAt",
                schema: "nhai",
                table: "AssistantAgent");

            migrationBuilder.DropColumn(
                name: "ShadowChangedBy",
                schema: "nhai",
                table: "AssistantAgent");

            migrationBuilder.DropColumn(
                name: "ShadowOverride",
                schema: "nhai",
                table: "AssistantAgent");

            migrationBuilder.DropColumn(
                name: "ShadowReason",
                schema: "nhai",
                table: "AssistantAgent");

            migrationBuilder.CreateIndex(
                name: "IX_AssistantThread_ScopeKey_AgentId_SubjectType_SubjectId",
                schema: "nhai",
                table: "AssistantThread",
                columns: new[] { "ScopeKey", "AgentId", "SubjectType", "SubjectId" },
                unique: true);
        }
    }
}
