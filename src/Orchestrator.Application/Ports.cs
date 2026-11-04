using Orchestrator.Domain;

namespace Orchestrator.Application;

public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

public interface IConversationStore
{
    Task CreateAsync(Conversation conversation, CancellationToken cancellationToken);
    Task<Conversation?> GetAsync(TenantId tenantId, ConversationId conversationId, CancellationToken cancellationToken);
    Task<IReadOnlyList<Conversation>> ListAsync(TenantId tenantId, UserId userId, CancellationToken cancellationToken);
    Task AddMessageAsync(ConversationMessage message, CancellationToken cancellationToken);
    Task<IReadOnlyList<ConversationMessage>> GetRecentMessagesAsync(
        TenantId tenantId,
        ConversationId conversationId,
        int limit,
        CancellationToken cancellationToken);
}

public interface IRunStore
{
    Task UpsertAsync(AgentRun run, CancellationToken cancellationToken);
    Task<AgentRun?> GetAsync(TenantId tenantId, RunId runId, CancellationToken cancellationToken);
    Task<bool> TryTransitionAsync(
        TenantId tenantId,
        RunId runId,
        IReadOnlySet<AgentRunStatus> expectedStatuses,
        AgentRun updated,
        CancellationToken cancellationToken);
}

public interface IFeedbackStore
{
    Task CreateAsync(RunFeedback feedback, CancellationToken cancellationToken);
}

public interface IIdempotencyStore
{
    Task<RunId?> FindAsync(TenantId tenantId, string key, CancellationToken cancellationToken);
    Task StoreAsync(TenantId tenantId, string key, RunId runId, DateTimeOffset expiresAt, CancellationToken cancellationToken);
}

public interface IAuditLog
{
    Task AppendAsync(AuditEntry entry, CancellationToken cancellationToken);
}

public interface IApprovalStore
{
    Task CreateAsync(ApprovalRequest approval, CancellationToken cancellationToken);
    Task<ApprovalRequest?> GetAsync(TenantId tenantId, ApprovalId approvalId, CancellationToken cancellationToken);
    Task<IReadOnlyList<ApprovalRequest>> ListPendingAsync(TenantId tenantId, CancellationToken cancellationToken);
    Task<bool> TryTransitionAsync(
        TenantId tenantId,
        ApprovalId approvalId,
        ApprovalStatus expectedStatus,
        ApprovalRequest updated,
        CancellationToken cancellationToken);
}

public interface IRunEventBroker
{
    Task PublishAsync(RunEvent runEvent, CancellationToken cancellationToken);
    IAsyncEnumerable<RunEvent> ReadAsync(TenantId tenantId, RunId runId, long afterSequence, CancellationToken cancellationToken);
}

public interface ISearchIndex
{
    Task<IReadOnlyList<SearchHit>> SearchAsync(SearchRequest request, CancellationToken cancellationToken);
}

public interface IDocumentIngestionGateway
{
    Task<DocumentIngestion> UploadAsync(DocumentUpload upload, CancellationToken cancellationToken);
    Task<DocumentIngestion?> GetAsync(TenantId tenantId, string ingestionId, CancellationToken cancellationToken);
    Task<bool> DeleteAsync(TenantId tenantId, string documentId, CancellationToken cancellationToken);
}

public interface IDocumentSource
{
    Task<IReadOnlyList<SourceDocument>> ListAsync(string? prefix, CancellationToken cancellationToken);
    Task<Stream> OpenReadAsync(string key, CancellationToken cancellationToken);
}

public interface IChatCompletionProvider
{
    Task<ChatCompletionResult> CompleteAsync(ChatCompletionRequest request, CancellationToken cancellationToken);
}

public interface IStructuredCompletionProvider
{
    Task<StructuredCompletionResult<T>> CompleteAsync<T>(
        StructuredCompletionRequest<T> request,
        CancellationToken cancellationToken)
        where T : class;
}

public interface IEmbeddingProvider
{
    Task<EmbeddingResult> EmbedAsync(string input, CancellationToken cancellationToken);
}

public interface IRerankingProvider
{
    Task<IReadOnlyList<SearchHit>> RerankAsync(string query, IReadOnlyList<SearchHit> hits, CancellationToken cancellationToken);
}

public interface ISafetyProvider
{
    Task<SafetyResult> EvaluateAsync(string input, CancellationToken cancellationToken);
}

public interface ITokenCounter
{
    int Count(string input);
}

public interface IAgentRuntime
{
    Task<AgentExecutionResult> ExecuteAsync(PreparedAgentRequest request, CancellationToken cancellationToken);
}

public interface IPolicyEvaluator
{
    Task<PolicyDecision> EvaluateAsync(PreparationContext context, CancellationToken cancellationToken);
}
