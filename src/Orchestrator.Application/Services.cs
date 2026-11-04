using Orchestrator.Domain;

namespace Orchestrator.Application;

public sealed class ConversationService
{
    private readonly IConversationStore _store;
    private readonly IClock _clock;

    public ConversationService(IConversationStore store, IClock clock)
    {
        _store = store;
        _clock = clock;
    }

    public async Task<Conversation> CreateAsync(UserContext user, string? title, CancellationToken cancellationToken)
    {
        var normalizedTitle = string.IsNullOrWhiteSpace(title) ? "New conversation" : title.Trim();
        if (normalizedTitle.Length > 200)
        {
            throw new DomainException(ErrorCategory.Validation, "conversation.title_length", "Conversation title is too long.");
        }

        var now = _clock.UtcNow;
        var conversation = new Conversation(
            ConversationId.New(),
            user.TenantId,
            user.UserId,
            normalizedTitle,
            now,
            now);
        await _store.CreateAsync(conversation, cancellationToken).ConfigureAwait(false);
        return conversation;
    }

    public Task<IReadOnlyList<Conversation>> ListAsync(UserContext user, CancellationToken cancellationToken) =>
        _store.ListAsync(user.TenantId, user.UserId, cancellationToken);
}

public sealed class AgentRunService
{
    private readonly RequestPreparationPipeline _preparation;
    private readonly IAgentRuntime _runtime;
    private readonly IConversationStore _conversations;
    private readonly IRunStore _runs;
    private readonly IIdempotencyStore _idempotency;
    private readonly IAuditLog _audit;
    private readonly IRunEventBroker _events;
    private readonly IClock _clock;

    public AgentRunService(
        RequestPreparationPipeline preparation,
        IAgentRuntime runtime,
        IConversationStore conversations,
        IRunStore runs,
        IIdempotencyStore idempotency,
        IAuditLog audit,
        IRunEventBroker events,
        IClock clock)
    {
        _preparation = preparation;
        _runtime = runtime;
        _conversations = conversations;
        _runs = runs;
        _idempotency = idempotency;
        _audit = audit;
        _events = events;
        _clock = clock;
    }

    public async Task<AgentRunOutcome> ExecuteAsync(AgentRequest request, CancellationToken cancellationToken)
    {
        ValidateIdempotencyKey(request.IdempotencyKey);
        var existingId = await _idempotency.FindAsync(
            request.User.TenantId,
            request.IdempotencyKey,
            cancellationToken).ConfigureAwait(false);
        if (existingId is { } runId)
        {
            var existing = await _runs.GetAsync(request.User.TenantId, runId, cancellationToken).ConfigureAwait(false);
            if (existing is not null)
            {
                return new AgentRunOutcome(existing, true);
            }
        }

        var now = _clock.UtcNow;
        var run = new AgentRun(
            RunId.New(),
            request.ConversationId,
            request.User.TenantId,
            request.User.UserId,
            AgentRunStatus.Accepted,
            null,
            null,
            now,
            now,
            request.User.CorrelationId,
            request.User.TraceId);

        await _runs.UpsertAsync(run, cancellationToken).ConfigureAwait(false);
        await _idempotency.StoreAsync(
            request.User.TenantId,
            request.IdempotencyKey,
            run.Id,
            now.AddHours(24),
            cancellationToken).ConfigureAwait(false);
        await PublishAsync(run, "accepted", new Dictionary<string, string>(), cancellationToken).ConfigureAwait(false);

        try
        {
            request = request with { RunId = run.Id };
            var prepared = await _preparation.PrepareAsync(request, cancellationToken).ConfigureAwait(false);
            await PublishAsync(run, "prepared", new Dictionary<string, string>
            {
                ["intent"] = prepared.Intent.Intent.ToString(),
                ["risk"] = prepared.InputRisk.ToString()
            }, cancellationToken).ConfigureAwait(false);

            await _conversations.AddMessageAsync(new ConversationMessage(
                MessageId.New(),
                request.ConversationId,
                request.User.TenantId,
                MessageRole.User,
                prepared.RedactedInput,
                _clock.UtcNow,
                run.Id), cancellationToken).ConfigureAwait(false);

            run = run with { Status = AgentRunStatus.Running, UpdatedAt = _clock.UtcNow };
            await _runs.UpsertAsync(run, cancellationToken).ConfigureAwait(false);
            var execution = await _runtime.ExecuteAsync(prepared, cancellationToken).ConfigureAwait(false);
            await PublishAsync(run, "agent_selected", new Dictionary<string, string>
            {
                ["agent"] = execution.AgentName
            }, cancellationToken).ConfigureAwait(false);

            if (execution.Approval is { } approval)
            {
                await _conversations.AddMessageAsync(new ConversationMessage(
                    MessageId.New(),
                    request.ConversationId,
                    request.User.TenantId,
                    MessageRole.Assistant,
                    execution.Answer.Text,
                    _clock.UtcNow,
                    run.Id), cancellationToken).ConfigureAwait(false);
                run = run with
                {
                    Status = AgentRunStatus.PendingApproval,
                    SelectedAgent = execution.AgentName,
                    Answer = execution.Answer,
                    UpdatedAt = _clock.UtcNow
                };
                await _runs.UpsertAsync(run, cancellationToken).ConfigureAwait(false);
                await PublishAsync(run, "approval_requested", new Dictionary<string, string>
                {
                    ["approval_id"] = approval.ApprovalId.ToString(),
                    ["tool"] = approval.ToolName,
                    ["expires_at"] = approval.ExpiresAt.ToString("O", System.Globalization.CultureInfo.InvariantCulture)
                }, cancellationToken).ConfigureAwait(false);
                await AppendAuditAsync(run, "agent.run", "pending_approval", cancellationToken).ConfigureAwait(false);
                return new AgentRunOutcome(run, false, approval);
            }

            foreach (var toolName in execution.UsedTools)
            {
                await PublishAsync(run, "tool_started", new Dictionary<string, string>
                {
                    ["tool"] = toolName
                }, cancellationToken).ConfigureAwait(false);
                await PublishAsync(run, "tool_completed", new Dictionary<string, string>
                {
                    ["tool"] = toolName,
                    ["outcome"] = "succeeded"
                }, cancellationToken).ConfigureAwait(false);
            }

            await _conversations.AddMessageAsync(new ConversationMessage(
                MessageId.New(),
                request.ConversationId,
                request.User.TenantId,
                MessageRole.Assistant,
                execution.Answer.Text,
                _clock.UtcNow,
                run.Id), cancellationToken).ConfigureAwait(false);

            run = run with
            {
                Status = AgentRunStatus.Completed,
                SelectedAgent = execution.AgentName,
                Answer = execution.Answer,
                UpdatedAt = _clock.UtcNow
            };
            await _runs.UpsertAsync(run, cancellationToken).ConfigureAwait(false);
            await PublishAsync(run, "completed", new Dictionary<string, string>
            {
                ["citations"] = execution.Answer.Citations.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["tools"] = string.Join(',', execution.UsedTools)
            }, cancellationToken).ConfigureAwait(false);
            await AppendAuditAsync(run, "agent.run", "completed", cancellationToken).ConfigureAwait(false);
            return new AgentRunOutcome(run, false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            var errorCode = exception is DomainException domainException ? domainException.Code : "run.failed";
            run = run with
            {
                Status = AgentRunStatus.Failed,
                ErrorCode = errorCode,
                UpdatedAt = _clock.UtcNow
            };
            await _runs.UpsertAsync(run, cancellationToken).ConfigureAwait(false);
            await PublishAsync(run, "failed", new Dictionary<string, string> { ["error_code"] = errorCode }, cancellationToken).ConfigureAwait(false);
            await AppendAuditAsync(run, "agent.run", "failed", cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    private async Task PublishAsync(
        AgentRun run,
        string type,
        IReadOnlyDictionary<string, string> data,
        CancellationToken cancellationToken)
    {
        await _events.PublishAsync(new RunEvent(
            0,
            run.Id,
            run.TenantId,
            type,
            _clock.UtcNow,
            data), cancellationToken).ConfigureAwait(false);
    }

    private Task AppendAuditAsync(AgentRun run, string action, string outcome, CancellationToken cancellationToken) =>
        _audit.AppendAsync(new AuditEntry(
            Guid.NewGuid(),
            run.TenantId,
            run.UserId,
            action,
            "agent_run",
            run.Id.ToString(),
            outcome,
            _clock.UtcNow,
            run.CorrelationId,
            new Dictionary<string, string>
            {
                ["status"] = run.Status.ToString(),
                ["agent"] = run.SelectedAgent ?? "none"
            }), cancellationToken);

    private static void ValidateIdempotencyKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length > 128)
        {
            throw new DomainException(ErrorCategory.Validation, "idempotency.invalid", "A valid idempotency key is required.");
        }
    }

    public async Task<AgentRun> CancelAsync(
        UserContext user,
        RunId runId,
        CancellationToken cancellationToken)
    {
        var run = await _runs.GetAsync(user.TenantId, runId, cancellationToken).ConfigureAwait(false)
            ?? throw new DomainException(ErrorCategory.NotFound, "run.not_found", "Agent run was not found.");
        if (run.Status is AgentRunStatus.Completed or AgentRunStatus.Failed or AgentRunStatus.Cancelled)
        {
            throw new DomainException(ErrorCategory.Conflict, "run.not_cancellable", "The agent run is already terminal.");
        }

        var cancelled = run with
        {
            Status = AgentRunStatus.Cancelled,
            ErrorCode = "run.cancelled_by_user",
            UpdatedAt = _clock.UtcNow
        };
        var changed = await _runs.TryTransitionAsync(
            user.TenantId,
            runId,
            new HashSet<AgentRunStatus>
            {
                AgentRunStatus.Accepted,
                AgentRunStatus.Running,
                AgentRunStatus.PendingApproval
            },
            cancelled,
            cancellationToken).ConfigureAwait(false);
        if (!changed)
        {
            throw new DomainException(ErrorCategory.Conflict, "run.cancel_race", "The agent run changed before cancellation.");
        }

        await PublishAsync(cancelled, "cancelled", new Dictionary<string, string>
        {
            ["reason"] = "cancelled_by_user"
        }, cancellationToken).ConfigureAwait(false);
        await AppendAuditAsync(cancelled, "agent.run.cancel", "cancelled", cancellationToken).ConfigureAwait(false);
        return cancelled;
    }
}

public sealed class FeedbackService
{
    private readonly IRunStore _runs;
    private readonly IFeedbackStore _feedback;
    private readonly IAuditLog _audit;
    private readonly IClock _clock;

    public FeedbackService(IRunStore runs, IFeedbackStore feedback, IAuditLog audit, IClock clock)
    {
        _runs = runs;
        _feedback = feedback;
        _audit = audit;
        _clock = clock;
    }

    public async Task<RunFeedback> CreateAsync(
        UserContext user,
        RunId runId,
        int rating,
        string? comment,
        CancellationToken cancellationToken)
    {
        if (rating is < -1 or > 1)
        {
            throw new DomainException(ErrorCategory.Validation, "feedback.rating", "Rating must be -1, 0, or 1.");
        }

        var normalizedComment = string.IsNullOrWhiteSpace(comment) ? null : comment.Trim();
        if (normalizedComment?.Length > 2_000)
        {
            throw new DomainException(ErrorCategory.Validation, "feedback.comment_length", "Feedback comment is too long.");
        }

        var run = await _runs.GetAsync(user.TenantId, runId, cancellationToken).ConfigureAwait(false)
            ?? throw new DomainException(ErrorCategory.NotFound, "run.not_found", "Agent run was not found.");
        if (run.UserId != user.UserId)
        {
            throw new DomainException(ErrorCategory.Authorization, "feedback.run_owner", "Feedback can only be submitted by the run owner.");
        }
        if (run.Status != AgentRunStatus.Completed)
        {
            throw new DomainException(ErrorCategory.Conflict, "feedback.run_not_completed", "Feedback requires a completed run.");
        }

        var feedback = new RunFeedback(
            Guid.NewGuid(), run.Id, run.ConversationId, run.TenantId, user.UserId,
            rating, normalizedComment, _clock.UtcNow);
        await _feedback.CreateAsync(feedback, cancellationToken).ConfigureAwait(false);
        await _audit.AppendAsync(new AuditEntry(
            Guid.NewGuid(), user.TenantId, user.UserId, "agent.run.feedback", "agent_run",
            run.Id.ToString(), "recorded", _clock.UtcNow, user.CorrelationId,
            new Dictionary<string, string> { ["rating"] = rating.ToString(System.Globalization.CultureInfo.InvariantCulture) }),
            cancellationToken).ConfigureAwait(false);
        return feedback;
    }
}
