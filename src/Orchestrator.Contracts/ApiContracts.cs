namespace Orchestrator.Contracts;

public sealed record DevelopmentTokenRequest(
    string? TenantId,
    string? UserId,
    IReadOnlyList<string>? Permissions,
    IReadOnlyList<string>? AccessTags);

public sealed record DevelopmentTokenResponse(string AccessToken, string TokenType, DateTimeOffset ExpiresAt);

public sealed record CreateConversationRequest(string? Title);

public sealed record ConversationResponse(
    Guid Id,
    string Title,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record SendMessageRequest(string Message);

public sealed record CitationResponse(
    string Id,
    string DocumentId,
    string ChunkId,
    string Title,
    string SourceUri,
    string? Locator,
    string Quote);

public sealed record ApprovalChallengeResponse(
    Guid ApprovalId,
    string ApprovalToken,
    string ToolName,
    DateTimeOffset ExpiresAt);

public sealed record AgentRunResponse(
    Guid RunId,
    Guid ConversationId,
    string Status,
    string? Agent,
    string? Answer,
    IReadOnlyList<CitationResponse> Citations,
    bool IsGrounded,
    bool IsNoAnswer,
    bool Replayed,
    ApprovalChallengeResponse? Approval,
    string CorrelationId,
    string TraceId,
    string? ErrorCode);

public sealed record SubmitFeedbackRequest(int Rating, string? Comment);

public sealed record FeedbackResponse(
    Guid FeedbackId,
    Guid RunId,
    int Rating,
    string? Comment,
    DateTimeOffset CreatedAt);

public sealed record ApprovalResponse(
    Guid ApprovalId,
    Guid RunId,
    Guid ConversationId,
    string ToolName,
    string ToolVersion,
    string Status,
    string RequestedBy,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    DateTimeOffset? DecidedAt,
    string? DecidedBy,
    DateTimeOffset? ExecutedAt,
    string? ErrorCode,
    string? RejectionReason);

public sealed record ApproveApprovalRequest(string ApprovalToken);

public sealed record RejectApprovalRequest(string? Reason);

public sealed record ApprovalDecisionResponse(ApprovalResponse Approval, AgentRunResponse Run);

public sealed record AgentCatalogItemResponse(
    string Name,
    string Description,
    string Version,
    IReadOnlyList<string> SupportedIntents,
    int MaxSteps,
    int TimeoutSeconds);

public sealed record ToolCatalogItemResponse(
    string Name,
    string Description,
    string Version,
    string InputSchema,
    string OutputSchema,
    IReadOnlyList<string> RequiredPermissions,
    string RiskLevel,
    string MutationBehavior,
    int TimeoutSeconds,
    int MaxRetries,
    string IdempotencyBehavior);

public sealed record DocumentIngestionResponse(
    string IngestionId,
    string DocumentId,
    string Status,
    string FileName,
    string ContentHash,
    int ChunkCount,
    string? ErrorCode,
    string? ErrorMessage,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt);
