namespace Orchestrator.Domain;

public enum MessageRole
{
    User,
    Assistant,
    System
}

public sealed record Conversation(
    ConversationId Id,
    TenantId TenantId,
    UserId CreatedBy,
    string Title,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record ConversationMessage(
    MessageId Id,
    ConversationId ConversationId,
    TenantId TenantId,
    MessageRole Role,
    string Content,
    DateTimeOffset CreatedAt,
    RunId? RunId = null);

public sealed record Citation(
    string CitationId,
    string DocumentId,
    string ChunkId,
    string Title,
    string SourceUri,
    string? Locator,
    string Quote);

public sealed record AgentAnswer(
    string Text,
    IReadOnlyList<Citation> Citations,
    bool IsGrounded,
    bool IsNoAnswer);
