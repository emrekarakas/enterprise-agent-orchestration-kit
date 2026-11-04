using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
#pragma warning disable CA1861

namespace Orchestrator.Infrastructure.Migrations;

/// <inheritdoc />
public partial class InitialCreate : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "agent_runs",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                ConversationId = table.Column<Guid>(type: "uuid", nullable: false),
                TenantId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                UserId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                SelectedAgent = table.Column<string>(type: "text", nullable: true),
                ErrorCode = table.Column<string>(type: "text", nullable: true),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                CorrelationId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                TraceId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                AnswerJson = table.Column<string>(type: "text", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_agent_runs", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "approval_requests",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                TenantId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                ConversationId = table.Column<Guid>(type: "uuid", nullable: false),
                RunId = table.Column<Guid>(type: "uuid", nullable: false),
                RequestedBy = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                ToolName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                ToolVersion = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                ArgumentsJson = table.Column<string>(type: "text", nullable: false),
                ArgumentsHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                TokenHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                IdempotencyKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                DecidedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                DecidedBy = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                ExecutedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                ResultJson = table.Column<string>(type: "text", nullable: true),
                ErrorCode = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                RejectionReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_approval_requests", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "audit_entries",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                TenantId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                UserId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                Action = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                ResourceType = table.Column<string>(type: "text", nullable: false),
                ResourceId = table.Column<string>(type: "text", nullable: false),
                Outcome = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                OccurredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                CorrelationId = table.Column<string>(type: "text", nullable: false),
                MetadataJson = table.Column<string>(type: "text", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_audit_entries", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "conversation_messages",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                ConversationId = table.Column<Guid>(type: "uuid", nullable: false),
                TenantId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                Role = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                Content = table.Column<string>(type: "character varying(16000)", maxLength: 16000, nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                RunId = table.Column<Guid>(type: "uuid", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_conversation_messages", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "conversations",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                TenantId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                CreatedBy = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                Title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_conversations", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "idempotency_keys",
            columns: table => new
            {
                TenantId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                Key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                RunId = table.Column<Guid>(type: "uuid", nullable: false),
                ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_idempotency_keys", x => new { x.TenantId, x.Key });
            });

        migrationBuilder.CreateIndex(
            name: "IX_agent_runs_TenantId_ConversationId_CreatedAt",
            table: "agent_runs",
            columns: new[] { "TenantId", "ConversationId", "CreatedAt" });

        migrationBuilder.CreateIndex(
            name: "IX_approval_requests_TenantId_RunId",
            table: "approval_requests",
            columns: new[] { "TenantId", "RunId" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_approval_requests_TenantId_Status_ExpiresAt",
            table: "approval_requests",
            columns: new[] { "TenantId", "Status", "ExpiresAt" });

        migrationBuilder.CreateIndex(
            name: "IX_audit_entries_TenantId_OccurredAt",
            table: "audit_entries",
            columns: new[] { "TenantId", "OccurredAt" });

        migrationBuilder.CreateIndex(
            name: "IX_conversation_messages_TenantId_ConversationId_CreatedAt",
            table: "conversation_messages",
            columns: new[] { "TenantId", "ConversationId", "CreatedAt" });

        migrationBuilder.CreateIndex(
            name: "IX_conversations_TenantId_CreatedBy_UpdatedAt",
            table: "conversations",
            columns: new[] { "TenantId", "CreatedBy", "UpdatedAt" });

        migrationBuilder.CreateIndex(
            name: "IX_idempotency_keys_ExpiresAt",
            table: "idempotency_keys",
            column: "ExpiresAt");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "agent_runs");

        migrationBuilder.DropTable(
            name: "approval_requests");

        migrationBuilder.DropTable(
            name: "audit_entries");

        migrationBuilder.DropTable(
            name: "conversation_messages");

        migrationBuilder.DropTable(
            name: "conversations");

        migrationBuilder.DropTable(
            name: "idempotency_keys");
    }
}
#pragma warning restore CA1861
