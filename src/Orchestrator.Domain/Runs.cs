namespace Orchestrator.Domain;

public enum AgentRunStatus
{
    Accepted,
    Running,
    PendingApproval,
    Completed,
    Failed,
    Cancelled
}

public sealed record AgentRun(
    RunId Id,
    ConversationId ConversationId,
    TenantId TenantId,
    UserId UserId,
    AgentRunStatus Status,
    string? SelectedAgent,
    string? ErrorCode,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string CorrelationId,
    string TraceId,
    AgentAnswer? Answer = null);

public sealed record RunEvent(
    long Sequence,
    RunId RunId,
    TenantId TenantId,
    string Type,
    DateTimeOffset OccurredAt,
    IReadOnlyDictionary<string, string> Data);

public sealed record AuditEntry(
    Guid Id,
    TenantId TenantId,
    UserId UserId,
    string Action,
    string ResourceType,
    string ResourceId,
    string Outcome,
    DateTimeOffset OccurredAt,
    string CorrelationId,
    IReadOnlyDictionary<string, string> Metadata);

public sealed record RunFeedback(
    Guid Id,
    RunId RunId,
    ConversationId ConversationId,
    TenantId TenantId,
    UserId UserId,
    int Rating,
    string? Comment,
    DateTimeOffset CreatedAt);
