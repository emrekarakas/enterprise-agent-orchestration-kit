using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Orchestrator.Application;
using Orchestrator.Domain;

namespace Orchestrator.Infrastructure;

public sealed class PostgresPlatformStore : IConversationStore, IRunStore, IIdempotencyStore, IApprovalStore, IFeedbackStore, IAuditLog
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly OrchestratorDbContext _dbContext;

    public PostgresPlatformStore(OrchestratorDbContext dbContext) => _dbContext = dbContext;

    public async Task CreateAsync(Conversation conversation, CancellationToken cancellationToken)
    {
        _dbContext.Conversations.Add(new ConversationRow
        {
            Id = conversation.Id.Value,
            TenantId = conversation.TenantId.Value,
            CreatedBy = conversation.CreatedBy.Value,
            Title = conversation.Title,
            CreatedAt = conversation.CreatedAt,
            UpdatedAt = conversation.UpdatedAt
        });
        await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<Conversation?> GetAsync(
        TenantId tenantId,
        ConversationId conversationId,
        CancellationToken cancellationToken)
    {
        var row = await _dbContext.Conversations.AsNoTracking()
            .SingleOrDefaultAsync(
                item => item.Id == conversationId.Value && item.TenantId == tenantId.Value,
                cancellationToken).ConfigureAwait(false);
        return row is null ? null : Map(row);
    }

    public async Task<IReadOnlyList<Conversation>> ListAsync(
        TenantId tenantId,
        UserId userId,
        CancellationToken cancellationToken)
    {
        var rows = await _dbContext.Conversations.AsNoTracking()
            .Where(item => item.TenantId == tenantId.Value && item.CreatedBy == userId.Value)
            .OrderByDescending(item => item.UpdatedAt)
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        return rows.Select(Map).ToArray();
    }

    public async Task AddMessageAsync(ConversationMessage message, CancellationToken cancellationToken)
    {
        _dbContext.Messages.Add(new MessageRow
        {
            Id = message.Id.Value,
            ConversationId = message.ConversationId.Value,
            TenantId = message.TenantId.Value,
            Role = message.Role.ToString(),
            Content = message.Content,
            CreatedAt = message.CreatedAt,
            RunId = message.RunId?.Value
        });

        var conversation = await _dbContext.Conversations.SingleOrDefaultAsync(
            item => item.Id == message.ConversationId.Value && item.TenantId == message.TenantId.Value,
            cancellationToken).ConfigureAwait(false);
        if (conversation is not null)
        {
            conversation.UpdatedAt = message.CreatedAt;
        }

        await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ConversationMessage>> GetRecentMessagesAsync(
        TenantId tenantId,
        ConversationId conversationId,
        int limit,
        CancellationToken cancellationToken)
    {
        var rows = await _dbContext.Messages.AsNoTracking()
            .Where(item => item.TenantId == tenantId.Value && item.ConversationId == conversationId.Value)
            .OrderByDescending(item => item.CreatedAt)
            .Take(limit)
            .OrderBy(item => item.CreatedAt)
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        return rows.Select(row => new ConversationMessage(
            new MessageId(row.Id),
            new ConversationId(row.ConversationId),
            TenantId.Create(row.TenantId),
            Enum.Parse<MessageRole>(row.Role, true),
            row.Content,
            row.CreatedAt,
            row.RunId is { } runId ? new RunId(runId) : null)).ToArray();
    }

    public async Task UpsertAsync(AgentRun run, CancellationToken cancellationToken)
    {
        var row = await _dbContext.Runs.SingleOrDefaultAsync(item => item.Id == run.Id.Value, cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            row = new RunRow { Id = run.Id.Value };
            _dbContext.Runs.Add(row);
        }

        row.ConversationId = run.ConversationId.Value;
        row.TenantId = run.TenantId.Value;
        row.UserId = run.UserId.Value;
        row.Status = run.Status.ToString();
        row.SelectedAgent = run.SelectedAgent;
        row.ErrorCode = run.ErrorCode;
        row.CreatedAt = run.CreatedAt;
        row.UpdatedAt = run.UpdatedAt;
        row.CorrelationId = run.CorrelationId;
        row.TraceId = run.TraceId;
        row.AnswerJson = run.Answer is null ? null : JsonSerializer.Serialize(run.Answer, JsonOptions);
        await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<AgentRun?> GetAsync(TenantId tenantId, RunId runId, CancellationToken cancellationToken)
    {
        var row = await _dbContext.Runs.AsNoTracking()
            .SingleOrDefaultAsync(
                item => item.Id == runId.Value && item.TenantId == tenantId.Value,
                cancellationToken).ConfigureAwait(false);
        return row is null ? null : Map(row);
    }

    public async Task<bool> TryTransitionAsync(
        TenantId tenantId,
        RunId runId,
        IReadOnlySet<AgentRunStatus> expectedStatuses,
        AgentRun updated,
        CancellationToken cancellationToken)
    {
        var statuses = expectedStatuses.Select(status => status.ToString()).ToArray();
        var affected = await _dbContext.Runs
            .Where(item => item.Id == runId.Value
                && item.TenantId == tenantId.Value
                && statuses.Contains(item.Status))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.Status, updated.Status.ToString())
                .SetProperty(item => item.ErrorCode, updated.ErrorCode)
                .SetProperty(item => item.UpdatedAt, updated.UpdatedAt),
                cancellationToken).ConfigureAwait(false);
        return affected == 1;
    }

    public async Task CreateAsync(RunFeedback feedback, CancellationToken cancellationToken)
    {
        _dbContext.Feedback.Add(new FeedbackRow
        {
            Id = feedback.Id,
            RunId = feedback.RunId.Value,
            ConversationId = feedback.ConversationId.Value,
            TenantId = feedback.TenantId.Value,
            UserId = feedback.UserId.Value,
            Rating = feedback.Rating,
            Comment = feedback.Comment,
            CreatedAt = feedback.CreatedAt
        });
        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException exception)
        {
            throw new DomainException(ErrorCategory.Conflict, "feedback.exists", "Feedback already exists for this run.", exception);
        }
    }

    public async Task<RunId?> FindAsync(TenantId tenantId, string key, CancellationToken cancellationToken)
    {
        var row = await _dbContext.IdempotencyKeys.AsNoTracking()
            .SingleOrDefaultAsync(
                item => item.TenantId == tenantId.Value && item.Key == key && item.ExpiresAt > DateTimeOffset.UtcNow,
                cancellationToken).ConfigureAwait(false);
        return row is null ? null : new RunId(row.RunId);
    }

    public async Task StoreAsync(
        TenantId tenantId,
        string key,
        RunId runId,
        DateTimeOffset expiresAt,
        CancellationToken cancellationToken)
    {
        _dbContext.IdempotencyKeys.Add(new IdempotencyRow
        {
            TenantId = tenantId.Value,
            Key = key,
            RunId = runId.Value,
            ExpiresAt = expiresAt
        });
        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException exception)
        {
            throw new DomainException(ErrorCategory.Conflict, "idempotency.conflict", "The idempotency key is already in use.", exception);
        }
    }

    public async Task AppendAsync(AuditEntry entry, CancellationToken cancellationToken)
    {
        _dbContext.AuditEntries.Add(new AuditRow
        {
            Id = entry.Id,
            TenantId = entry.TenantId.Value,
            UserId = entry.UserId.Value,
            Action = entry.Action,
            ResourceType = entry.ResourceType,
            ResourceId = entry.ResourceId,
            Outcome = entry.Outcome,
            OccurredAt = entry.OccurredAt,
            CorrelationId = entry.CorrelationId,
            MetadataJson = JsonSerializer.Serialize(entry.Metadata, JsonOptions)
        });
        await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task CreateAsync(ApprovalRequest approval, CancellationToken cancellationToken)
    {
        _dbContext.Approvals.Add(Map(approval));
        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException exception)
        {
            throw new DomainException(ErrorCategory.Conflict, "approval.exists", "The approval request already exists.", exception);
        }
    }

    public async Task<ApprovalRequest?> GetAsync(
        TenantId tenantId,
        ApprovalId approvalId,
        CancellationToken cancellationToken)
    {
        var row = await _dbContext.Approvals.AsNoTracking()
            .SingleOrDefaultAsync(
                item => item.Id == approvalId.Value && item.TenantId == tenantId.Value,
                cancellationToken).ConfigureAwait(false);
        return row is null ? null : Map(row);
    }

    public async Task<IReadOnlyList<ApprovalRequest>> ListPendingAsync(
        TenantId tenantId,
        CancellationToken cancellationToken)
    {
        var rows = await _dbContext.Approvals.AsNoTracking()
            .Where(item => item.TenantId == tenantId.Value && item.Status == ApprovalStatus.Pending.ToString())
            .OrderBy(item => item.ExpiresAt)
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        return rows.Select(Map).ToArray();
    }

    public async Task<bool> TryTransitionAsync(
        TenantId tenantId,
        ApprovalId approvalId,
        ApprovalStatus expectedStatus,
        ApprovalRequest updated,
        CancellationToken cancellationToken)
    {
        var affected = await _dbContext.Approvals
            .Where(item => item.Id == approvalId.Value
                && item.TenantId == tenantId.Value
                && item.Status == expectedStatus.ToString())
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.Status, updated.Status.ToString())
                .SetProperty(item => item.DecidedAt, updated.DecidedAt)
                .SetProperty(item => item.DecidedBy, updated.DecidedBy.HasValue ? updated.DecidedBy.Value.Value : null)
                .SetProperty(item => item.ExecutedAt, updated.ExecutedAt)
                .SetProperty(item => item.ResultJson, updated.ResultJson)
                .SetProperty(item => item.ErrorCode, updated.ErrorCode)
                .SetProperty(item => item.RejectionReason, updated.RejectionReason),
                cancellationToken).ConfigureAwait(false);
        return affected == 1;
    }

    private static Conversation Map(ConversationRow row) => new(
        new ConversationId(row.Id),
        TenantId.Create(row.TenantId),
        UserId.Create(row.CreatedBy),
        row.Title,
        row.CreatedAt,
        row.UpdatedAt);

    private static AgentRun Map(RunRow row) => new(
        new RunId(row.Id),
        new ConversationId(row.ConversationId),
        TenantId.Create(row.TenantId),
        UserId.Create(row.UserId),
        Enum.Parse<AgentRunStatus>(row.Status, true),
        row.SelectedAgent,
        row.ErrorCode,
        row.CreatedAt,
        row.UpdatedAt,
        row.CorrelationId,
        row.TraceId,
        row.AnswerJson is null ? null : JsonSerializer.Deserialize<AgentAnswer>(row.AnswerJson, JsonOptions));

    private static ApprovalRow Map(ApprovalRequest approval) => new()
    {
        Id = approval.Id.Value,
        TenantId = approval.TenantId.Value,
        ConversationId = approval.ConversationId.Value,
        RunId = approval.RunId.Value,
        RequestedBy = approval.RequestedBy.Value,
        ToolName = approval.ToolName,
        ToolVersion = approval.ToolVersion,
        ArgumentsJson = approval.ArgumentsJson,
        ArgumentsHash = approval.ArgumentsHash,
        TokenHash = approval.TokenHash,
        IdempotencyKey = approval.IdempotencyKey,
        Status = approval.Status.ToString(),
        CreatedAt = approval.CreatedAt,
        ExpiresAt = approval.ExpiresAt,
        DecidedAt = approval.DecidedAt,
        DecidedBy = approval.DecidedBy?.Value,
        ExecutedAt = approval.ExecutedAt,
        ResultJson = approval.ResultJson,
        ErrorCode = approval.ErrorCode,
        RejectionReason = approval.RejectionReason
    };

    private static ApprovalRequest Map(ApprovalRow row) => new(
        new ApprovalId(row.Id),
        TenantId.Create(row.TenantId),
        new ConversationId(row.ConversationId),
        new RunId(row.RunId),
        UserId.Create(row.RequestedBy),
        row.ToolName,
        row.ToolVersion,
        row.ArgumentsJson,
        row.ArgumentsHash,
        row.TokenHash,
        row.IdempotencyKey,
        Enum.Parse<ApprovalStatus>(row.Status, true),
        row.CreatedAt,
        row.ExpiresAt,
        row.DecidedAt,
        row.DecidedBy is null ? null : UserId.Create(row.DecidedBy),
        row.ExecutedAt,
        row.ResultJson,
        row.ErrorCode,
        row.RejectionReason);
}
