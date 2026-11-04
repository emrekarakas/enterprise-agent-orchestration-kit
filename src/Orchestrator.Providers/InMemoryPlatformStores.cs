using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Orchestrator.Application;
using Orchestrator.Domain;

namespace Orchestrator.Providers;

public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

public sealed class InMemoryPlatformStores :
    IConversationStore,
    IRunStore,
    IIdempotencyStore,
    IApprovalStore,
    IFeedbackStore,
    IAuditLog,
    IRunEventBroker
{
    private readonly ConcurrentDictionary<ConversationId, Conversation> _conversations = new();
    private readonly ConcurrentDictionary<ConversationId, List<ConversationMessage>> _messages = new();
    private readonly ConcurrentDictionary<RunId, AgentRun> _runs = new();
    private readonly ConcurrentDictionary<ApprovalId, ApprovalRequest> _approvals = new();
    private readonly ConcurrentDictionary<string, (RunId RunId, DateTimeOffset ExpiresAt)> _idempotency = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<RunId, List<RunEvent>> _events = new();
    private readonly ConcurrentQueue<AuditEntry> _audit = new();
    private readonly ConcurrentDictionary<(RunId RunId, UserId UserId), RunFeedback> _feedback = new();

    public Task CreateAsync(Conversation conversation, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_conversations.TryAdd(conversation.Id, conversation))
        {
            throw new DomainException(ErrorCategory.Conflict, "conversation.exists", "Conversation already exists.");
        }

        return Task.CompletedTask;
    }

    public Task<Conversation?> GetAsync(TenantId tenantId, ConversationId conversationId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _conversations.TryGetValue(conversationId, out var conversation);
        return Task.FromResult(conversation?.TenantId == tenantId ? conversation : null);
    }

    public Task<IReadOnlyList<Conversation>> ListAsync(TenantId tenantId, UserId userId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = _conversations.Values
            .Where(conversation => conversation.TenantId == tenantId && conversation.CreatedBy == userId)
            .OrderByDescending(conversation => conversation.UpdatedAt)
            .ToArray();
        return Task.FromResult<IReadOnlyList<Conversation>>(result);
    }

    public Task AddMessageAsync(ConversationMessage message, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var list = _messages.GetOrAdd(message.ConversationId, _ => []);
        lock (list)
        {
            list.Add(message);
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ConversationMessage>> GetRecentMessagesAsync(
        TenantId tenantId,
        ConversationId conversationId,
        int limit,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_conversations.TryGetValue(conversationId, out var conversation) || conversation.TenantId != tenantId)
        {
            return Task.FromResult<IReadOnlyList<ConversationMessage>>([]);
        }

        if (!_messages.TryGetValue(conversationId, out var list))
        {
            return Task.FromResult<IReadOnlyList<ConversationMessage>>([]);
        }

        lock (list)
        {
            return Task.FromResult<IReadOnlyList<ConversationMessage>>(list.TakeLast(limit).ToArray());
        }
    }

    public Task UpsertAsync(AgentRun run, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _runs[run.Id] = run;
        return Task.CompletedTask;
    }

    public Task<AgentRun?> GetAsync(TenantId tenantId, RunId runId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _runs.TryGetValue(runId, out var run);
        return Task.FromResult(run?.TenantId == tenantId ? run : null);
    }

    public Task<bool> TryTransitionAsync(
        TenantId tenantId,
        RunId runId,
        IReadOnlySet<AgentRunStatus> expectedStatuses,
        AgentRun updated,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        while (_runs.TryGetValue(runId, out var current))
        {
            if (current.TenantId != tenantId || !expectedStatuses.Contains(current.Status))
            {
                return Task.FromResult(false);
            }

            if (_runs.TryUpdate(runId, updated, current))
            {
                return Task.FromResult(true);
            }
        }

        return Task.FromResult(false);
    }

    public Task CreateAsync(RunFeedback feedback, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_feedback.TryAdd((feedback.RunId, feedback.UserId), feedback))
        {
            throw new DomainException(ErrorCategory.Conflict, "feedback.exists", "Feedback already exists for this run.");
        }

        return Task.CompletedTask;
    }

    public Task<RunId?> FindAsync(TenantId tenantId, string key, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var composite = $"{tenantId.Value}:{key}";
        if (_idempotency.TryGetValue(composite, out var entry) && entry.ExpiresAt > DateTimeOffset.UtcNow)
        {
            return Task.FromResult<RunId?>(entry.RunId);
        }

        return Task.FromResult<RunId?>(null);
    }

    public Task StoreAsync(
        TenantId tenantId,
        string key,
        RunId runId,
        DateTimeOffset expiresAt,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var composite = $"{tenantId.Value}:{key}";
        if (!_idempotency.TryAdd(composite, (runId, expiresAt)))
        {
            throw new DomainException(ErrorCategory.Conflict, "idempotency.conflict", "The idempotency key is already in use.");
        }

        return Task.CompletedTask;
    }

    public Task AppendAsync(AuditEntry entry, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _audit.Enqueue(entry);
        return Task.CompletedTask;
    }

    public Task CreateAsync(ApprovalRequest approval, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_approvals.TryAdd(approval.Id, approval))
        {
            throw new DomainException(ErrorCategory.Conflict, "approval.exists", "The approval request already exists.");
        }

        return Task.CompletedTask;
    }

    public Task<ApprovalRequest?> GetAsync(
        TenantId tenantId,
        ApprovalId approvalId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _approvals.TryGetValue(approvalId, out var approval);
        return Task.FromResult(approval?.TenantId == tenantId ? approval : null);
    }

    public Task<IReadOnlyList<ApprovalRequest>> ListPendingAsync(
        TenantId tenantId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = _approvals.Values
            .Where(item => item.TenantId == tenantId && item.Status == ApprovalStatus.Pending)
            .OrderBy(item => item.ExpiresAt)
            .ToArray();
        return Task.FromResult<IReadOnlyList<ApprovalRequest>>(result);
    }

    public Task<bool> TryTransitionAsync(
        TenantId tenantId,
        ApprovalId approvalId,
        ApprovalStatus expectedStatus,
        ApprovalRequest updated,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        while (_approvals.TryGetValue(approvalId, out var current))
        {
            if (current.TenantId != tenantId || current.Status != expectedStatus)
            {
                return Task.FromResult(false);
            }

            if (_approvals.TryUpdate(approvalId, updated, current))
            {
                return Task.FromResult(true);
            }
        }

        return Task.FromResult(false);
    }

    public Task PublishAsync(RunEvent runEvent, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var list = _events.GetOrAdd(runEvent.RunId, _ => []);
        lock (list)
        {
            list.Add(runEvent with { Sequence = list.Count + 1L });
        }

        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<RunEvent> ReadAsync(
        TenantId tenantId,
        RunId runId,
        long afterSequence,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var next = afterSequence + 1;
        while (!cancellationToken.IsCancellationRequested)
        {
            RunEvent[] snapshot;
            if (_events.TryGetValue(runId, out var list))
            {
                lock (list)
                {
                    snapshot = list.Where(item => item.Sequence >= next && item.TenantId == tenantId).ToArray();
                }
            }
            else
            {
                snapshot = [];
            }

            foreach (var item in snapshot)
            {
                next = item.Sequence + 1;
                yield return item;
            }

            if (snapshot.Any(item => item.Type is "completed" or "failed" or "cancelled"))
            {
                yield break;
            }

            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }
    }
}
