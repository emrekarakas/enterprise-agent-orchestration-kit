using Orchestrator.Application;
using Orchestrator.Contracts;
using Orchestrator.Domain;

namespace Orchestrator.Api;

public static class ContractMappings
{
    public static ConversationResponse ToResponse(this Conversation conversation) => new(
        conversation.Id.Value,
        conversation.Title,
        conversation.CreatedAt,
        conversation.UpdatedAt);

    public static AgentRunResponse ToResponse(this AgentRunOutcome outcome)
    {
        var answer = outcome.Run.Answer;
        return new AgentRunResponse(
            outcome.Run.Id.Value,
            outcome.Run.ConversationId.Value,
            outcome.Run.Status.ToString(),
            outcome.Run.SelectedAgent,
            answer?.Text,
            answer?.Citations.Select(citation => new CitationResponse(
                citation.CitationId,
                citation.DocumentId,
                citation.ChunkId,
                citation.Title,
                citation.SourceUri,
                citation.Locator,
                citation.Quote)).ToArray() ?? [],
            answer?.IsGrounded ?? false,
            answer?.IsNoAnswer ?? false,
            outcome.Replayed,
            outcome.Approval is null ? null : new ApprovalChallengeResponse(
                outcome.Approval.ApprovalId.Value,
                outcome.Approval.ApprovalToken,
                outcome.Approval.ToolName,
                outcome.Approval.ExpiresAt),
            outcome.Run.CorrelationId,
            outcome.Run.TraceId,
            outcome.Run.ErrorCode);
    }

    public static ApprovalResponse ToResponse(this ApprovalRequest approval) => new(
        approval.Id.Value,
        approval.RunId.Value,
        approval.ConversationId.Value,
        approval.ToolName,
        approval.ToolVersion,
        approval.Status.ToString(),
        approval.RequestedBy.Value,
        approval.CreatedAt,
        approval.ExpiresAt,
        approval.DecidedAt,
        approval.DecidedBy?.Value,
        approval.ExecutedAt,
        approval.ErrorCode,
        approval.RejectionReason);

    public static DocumentIngestionResponse ToResponse(this DocumentIngestion ingestion) => new(
        ingestion.IngestionId,
        ingestion.DocumentId,
        ingestion.Status,
        ingestion.FileName,
        ingestion.ContentHash,
        ingestion.ChunkCount,
        ingestion.ErrorCode,
        ingestion.ErrorMessage,
        ingestion.CreatedAt,
        ingestion.CompletedAt);
}
