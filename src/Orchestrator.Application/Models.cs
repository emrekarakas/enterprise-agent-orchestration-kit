using System.Text.Json;
using Orchestrator.Domain;

namespace Orchestrator.Application;

public sealed record UserContext(
    TenantId TenantId,
    UserId UserId,
    IReadOnlySet<string> Permissions,
    IReadOnlySet<string> AccessTags,
    string CorrelationId,
    string TraceId);

public sealed record AgentRequest(
    ConversationId ConversationId,
    string Input,
    UserContext User,
    string IdempotencyKey,
    RunId? RunId = null);

public enum IntentCategory
{
    Knowledge,
    DocumentAnalysis,
    Workflow,
    Conversation,
    Unknown
}

public sealed record IntentDecision(IntentCategory Intent, double Confidence, string ReasonCode);

public sealed record RouteDecision(
    IReadOnlyList<string> AgentNames,
    bool RunInParallel,
    int MaxSteps,
    string ReasonCode);

public sealed record TicketProposal(string Subject, string Description, string Priority);

public enum InputRisk
{
    None,
    Suspicious,
    Blocked
}

public sealed record PolicyDecision(bool Allowed, string ReasonCode, IReadOnlySet<string> AllowedTools);

public sealed record PreparedAgentRequest(
    AgentRequest Original,
    string NormalizedInput,
    string RedactedInput,
    IReadOnlyList<ConversationMessage> RelevantHistory,
    IntentDecision Intent,
    InputRisk InputRisk,
    PolicyDecision Policy);

public sealed record SearchRequest(
    TenantId TenantId,
    string Query,
    IReadOnlySet<string> AccessTags,
    int Top,
    double MinimumScore);

public sealed record SearchHit(
    string DocumentId,
    string ChunkId,
    string Title,
    string SourceUri,
    string? Locator,
    string Content,
    double Score,
    TenantId TenantId,
    IReadOnlyList<string> AccessTags);

public sealed record DocumentUpload(
    TenantId TenantId,
    UserId UserId,
    string FileName,
    string ContentType,
    Stream Content,
    long Length,
    IReadOnlySet<string> AccessTags,
    string? SourceUri);

public sealed record DocumentIngestion(
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

public sealed record SourceDocument(
    string Key,
    string SourceUri,
    string FileName,
    string ETag,
    long Length,
    DateTimeOffset? LastModified,
    IReadOnlyDictionary<string, string> Metadata);

public sealed record ChatCompletionRequest(
    string Operation,
    string SystemInstruction,
    string Input,
    IReadOnlyList<ConversationMessage> History,
    int MaxOutputTokens,
    double Temperature);

public sealed record ChatCompletionResult(string Text, int InputTokens, int OutputTokens, string Model);

public sealed record StructuredCompletionRequest<T>(
    string Operation,
    string Input,
    string JsonSchema,
    T DeterministicFallback)
    where T : class;

public sealed record StructuredCompletionResult<T>(T Value, bool UsedFallback, string Model)
    where T : class;

public sealed record EmbeddingResult(IReadOnlyList<float> Vector, int TokenCount, string Model);

public sealed record SafetyResult(bool Allowed, string Category, double Confidence);

public sealed record AgentExecutionResult(
    string AgentName,
    AgentAnswer Answer,
    IReadOnlyList<SearchHit> Evidence,
    IReadOnlyList<string> UsedTools,
    int Steps,
    ApprovalChallenge? Approval = null);

public sealed record ApprovalChallenge(
    ApprovalId ApprovalId,
    string ApprovalToken,
    string ToolName,
    DateTimeOffset ExpiresAt);

public sealed record ApprovalExecutionOutcome(ApprovalRequest Approval, AgentRun Run);

public enum ToolRiskLevel
{
    Low,
    Medium,
    High,
    Critical
}

public enum ToolMutationBehavior
{
    ReadOnly,
    StateChanging
}

public enum ToolIdempotencyBehavior
{
    NotApplicable,
    Optional,
    Required
}

public sealed record ToolDescriptor(
    string Name,
    string Description,
    string Version,
    string InputSchema,
    string OutputSchema,
    IReadOnlySet<string> RequiredPermissions,
    ToolRiskLevel RiskLevel,
    ToolMutationBehavior MutationBehavior,
    TimeSpan Timeout,
    int MaxRetries,
    ToolIdempotencyBehavior IdempotencyBehavior);

public sealed record ToolExecutionContext(
    PreparedAgentRequest Request,
    string InvocationId,
    string? IdempotencyKey);

public sealed record ToolResult(
    bool Succeeded,
    JsonElement Output,
    string? ErrorCode,
    bool RequiresApproval);

public sealed record AgentRunOutcome(AgentRun Run, bool Replayed, ApprovalChallenge? Approval = null);
