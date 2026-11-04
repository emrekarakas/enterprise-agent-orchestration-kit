namespace Orchestrator.Domain;

public enum ApprovalStatus
{
    Pending,
    Executing,
    Executed,
    Rejected,
    Expired,
    Failed
}

public sealed record ApprovalRequest(
    ApprovalId Id,
    TenantId TenantId,
    ConversationId ConversationId,
    RunId RunId,
    UserId RequestedBy,
    string ToolName,
    string ToolVersion,
    string ArgumentsJson,
    string ArgumentsHash,
    string TokenHash,
    string IdempotencyKey,
    ApprovalStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    DateTimeOffset? DecidedAt = null,
    UserId? DecidedBy = null,
    DateTimeOffset? ExecutedAt = null,
    string? ResultJson = null,
    string? ErrorCode = null,
    string? RejectionReason = null);
