using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Orchestrator.Domain;

namespace Orchestrator.Application;

public sealed class ApprovalService
{
    private static readonly TimeSpan ApprovalLifetime = TimeSpan.FromMinutes(15);
    private readonly IApprovalStore _approvals;
    private readonly IRunStore _runs;
    private readonly IConversationStore _conversations;
    private readonly IAuditLog _audit;
    private readonly IRunEventBroker _events;
    private readonly ToolRegistry _tools;
    private readonly ToolDispatcher _dispatcher;
    private readonly IClock _clock;

    public ApprovalService(
        IApprovalStore approvals,
        IRunStore runs,
        IConversationStore conversations,
        IAuditLog audit,
        IRunEventBroker events,
        ToolRegistry tools,
        ToolDispatcher dispatcher,
        IClock clock)
    {
        _approvals = approvals;
        _runs = runs;
        _conversations = conversations;
        _audit = audit;
        _events = events;
        _tools = tools;
        _dispatcher = dispatcher;
        _clock = clock;
    }

    public async Task<ApprovalChallenge> CreateAsync(
        PreparedAgentRequest request,
        string toolName,
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        if (request.Original.RunId is not { } runId)
        {
            throw new DomainException(ErrorCategory.Internal, "approval.run_missing", "The run context is missing.");
        }

        if (!_tools.TryGet(toolName, out var tool) || tool is null)
        {
            throw new DomainException(ErrorCategory.Validation, "tool.unknown", "The requested tool is not registered.");
        }

        if (tool.Descriptor.MutationBehavior != ToolMutationBehavior.StateChanging)
        {
            throw new DomainException(ErrorCategory.Validation, "approval.not_required", "The requested tool does not require approval.");
        }

        if (!request.Policy.AllowedTools.Contains(toolName)
            || !tool.Descriptor.RequiredPermissions.IsSubsetOf(request.Original.User.Permissions))
        {
            throw new DomainException(ErrorCategory.Authorization, "tool.permission_denied", "The caller is not authorized to propose this tool operation.");
        }

        if (!tool.ValidateArguments(arguments))
        {
            throw new DomainException(ErrorCategory.Validation, "tool.arguments_invalid", "The proposed tool arguments are invalid.");
        }

        var token = CreateToken();
        var argumentsJson = arguments.GetRawText();
        var now = _clock.UtcNow;
        var approval = new ApprovalRequest(
            ApprovalId.New(),
            request.Original.User.TenantId,
            request.Original.ConversationId,
            runId,
            request.Original.User.UserId,
            tool.Descriptor.Name,
            tool.Descriptor.Version,
            argumentsJson,
            Hash(argumentsJson),
            Hash(token),
            $"approval:{runId}:{tool.Descriptor.Name}",
            ApprovalStatus.Pending,
            now,
            now.Add(ApprovalLifetime));
        await _approvals.CreateAsync(approval, cancellationToken).ConfigureAwait(false);
        await AppendAuditAsync(approval, request.Original.User, "approval.requested", "pending", cancellationToken).ConfigureAwait(false);
        return new ApprovalChallenge(approval.Id, token, approval.ToolName, approval.ExpiresAt);
    }

    public async Task<IReadOnlyList<ApprovalRequest>> ListPendingAsync(
        UserContext user,
        CancellationToken cancellationToken)
    {
        RequireApprovalPermission(user);
        var pending = await _approvals.ListPendingAsync(user.TenantId, cancellationToken).ConfigureAwait(false);
        var result = new List<ApprovalRequest>(pending.Count);
        foreach (var approval in pending)
        {
            if (approval.ExpiresAt <= _clock.UtcNow)
            {
                var expired = approval with { Status = ApprovalStatus.Expired, DecidedAt = _clock.UtcNow };
                await _approvals.TryTransitionAsync(
                    user.TenantId,
                    approval.Id,
                    ApprovalStatus.Pending,
                    expired,
                    cancellationToken).ConfigureAwait(false);
                continue;
            }

            result.Add(approval);
        }

        return result;
    }

    public async Task<ApprovalExecutionOutcome> ApproveAsync(
        UserContext approver,
        ApprovalId approvalId,
        string approvalToken,
        CancellationToken cancellationToken)
    {
        RequireApprovalPermission(approver);
        if (string.IsNullOrWhiteSpace(approvalToken) || approvalToken.Length > 512)
        {
            throw new DomainException(ErrorCategory.Validation, "approval.token_invalid", "A valid approval token is required.");
        }

        var approval = await GetRequiredAsync(approver.TenantId, approvalId, cancellationToken).ConfigureAwait(false);
        EnsurePending(approval);
        if (approval.ExpiresAt <= _clock.UtcNow)
        {
            var expired = approval with { Status = ApprovalStatus.Expired, DecidedAt = _clock.UtcNow, DecidedBy = approver.UserId };
            await _approvals.TryTransitionAsync(
                approver.TenantId,
                approval.Id,
                ApprovalStatus.Pending,
                expired,
                cancellationToken).ConfigureAwait(false);
            throw new DomainException(ErrorCategory.Conflict, "approval.expired", "The approval request has expired.");
        }

        if (!FixedTimeEquals(approval.TokenHash, Hash(approvalToken)))
        {
            throw new DomainException(ErrorCategory.Authorization, "approval.token_invalid", "The approval token is invalid.");
        }

        if (!FixedTimeEquals(approval.ArgumentsHash, Hash(approval.ArgumentsJson)))
        {
            throw new DomainException(ErrorCategory.Conflict, "approval.arguments_changed", "The approved parameters failed their integrity check.");
        }

        var relatedRun = await GetRunRequiredAsync(approval, cancellationToken).ConfigureAwait(false);
        if (relatedRun.Status != AgentRunStatus.PendingApproval)
        {
            throw new DomainException(
                ErrorCategory.Conflict,
                "approval.run_not_pending",
                "The related run is no longer waiting for approval.");
        }

        var executing = approval with
        {
            Status = ApprovalStatus.Executing,
            DecidedAt = _clock.UtcNow,
            DecidedBy = approver.UserId
        };
        var acquired = await _approvals.TryTransitionAsync(
            approver.TenantId,
            approval.Id,
            ApprovalStatus.Pending,
            executing,
            cancellationToken).ConfigureAwait(false);
        if (!acquired)
        {
            throw new DomainException(ErrorCategory.Conflict, "approval.already_decided", "The approval request has already been decided.");
        }

        try
        {
            using var document = JsonDocument.Parse(approval.ArgumentsJson);
            var result = await _dispatcher.DispatchApprovedAsync(
                executing,
                approver,
                document.RootElement.Clone(),
                cancellationToken).ConfigureAwait(false);
            if (!result.Succeeded)
            {
                throw new DomainException(ErrorCategory.ExternalDependency, result.ErrorCode ?? "tool.execution_failed", "The approved tool could not complete.");
            }

            var completedAt = _clock.UtcNow;
            var executed = executing with
            {
                Status = ApprovalStatus.Executed,
                ExecutedAt = completedAt,
                ResultJson = result.Output.GetRawText()
            };
            if (!await _approvals.TryTransitionAsync(
                    approver.TenantId,
                    approval.Id,
                    ApprovalStatus.Executing,
                    executed,
                    cancellationToken).ConfigureAwait(false))
            {
                throw new DomainException(ErrorCategory.Conflict, "approval.execution_state", "The approval execution state changed unexpectedly.");
            }

            var run = await GetRunRequiredAsync(approval, cancellationToken).ConfigureAwait(false);
            var answer = new AgentAnswer(FormatExecutionAnswer(approval.ToolName, result.Output), [], false, false);
            run = run with
            {
                Status = AgentRunStatus.Completed,
                Answer = answer,
                UpdatedAt = completedAt
            };
            await _runs.UpsertAsync(run, cancellationToken).ConfigureAwait(false);
            await _conversations.AddMessageAsync(new ConversationMessage(
                MessageId.New(),
                run.ConversationId,
                run.TenantId,
                MessageRole.Assistant,
                answer.Text,
                completedAt,
                run.Id), cancellationToken).ConfigureAwait(false);
            await PublishAsync(run, "approval_executed", approval, cancellationToken).ConfigureAwait(false);
            await PublishAsync(run, "completed", approval, cancellationToken).ConfigureAwait(false);
            await AppendAuditAsync(executed, approver, "approval.executed", "succeeded", cancellationToken).ConfigureAwait(false);
            return new ApprovalExecutionOutcome(executed, run);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            var failed = executing with
            {
                Status = ApprovalStatus.Failed,
                ExecutedAt = _clock.UtcNow,
                ErrorCode = exception is DomainException domain ? domain.Code : "tool.execution_failed"
            };
            await _approvals.TryTransitionAsync(
                approver.TenantId,
                approval.Id,
                ApprovalStatus.Executing,
                failed,
                CancellationToken.None).ConfigureAwait(false);
            var run = await GetRunRequiredAsync(approval, CancellationToken.None).ConfigureAwait(false);
            run = run with { Status = AgentRunStatus.Failed, ErrorCode = failed.ErrorCode, UpdatedAt = _clock.UtcNow };
            await _runs.UpsertAsync(run, CancellationToken.None).ConfigureAwait(false);
            await PublishAsync(run, "failed", approval, CancellationToken.None).ConfigureAwait(false);
            await AppendAuditAsync(failed, approver, "approval.execution_failed", "failed", CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async Task<ApprovalExecutionOutcome> RejectAsync(
        UserContext approver,
        ApprovalId approvalId,
        string? reason,
        CancellationToken cancellationToken)
    {
        RequireApprovalPermission(approver);
        var approval = await GetRequiredAsync(approver.TenantId, approvalId, cancellationToken).ConfigureAwait(false);
        EnsurePending(approval);
        var rejected = approval with
        {
            Status = ApprovalStatus.Rejected,
            DecidedAt = _clock.UtcNow,
            DecidedBy = approver.UserId,
            RejectionReason = NormalizeReason(reason)
        };
        if (!await _approvals.TryTransitionAsync(
                approver.TenantId,
                approval.Id,
                ApprovalStatus.Pending,
                rejected,
                cancellationToken).ConfigureAwait(false))
        {
            throw new DomainException(ErrorCategory.Conflict, "approval.already_decided", "The approval request has already been decided.");
        }

        var run = await GetRunRequiredAsync(approval, cancellationToken).ConfigureAwait(false);
        var answer = new AgentAnswer("The proposed operation was rejected and was not executed.", [], false, false);
        run = run with { Status = AgentRunStatus.Cancelled, Answer = answer, UpdatedAt = _clock.UtcNow };
        await _runs.UpsertAsync(run, cancellationToken).ConfigureAwait(false);
        await PublishAsync(run, "cancelled", approval, cancellationToken).ConfigureAwait(false);
        await AppendAuditAsync(rejected, approver, "approval.rejected", "rejected", cancellationToken).ConfigureAwait(false);
        return new ApprovalExecutionOutcome(rejected, run);
    }

    private async Task<ApprovalRequest> GetRequiredAsync(
        TenantId tenantId,
        ApprovalId approvalId,
        CancellationToken cancellationToken) =>
        await _approvals.GetAsync(tenantId, approvalId, cancellationToken).ConfigureAwait(false)
        ?? throw new DomainException(ErrorCategory.NotFound, "approval.not_found", "The approval request was not found.");

    private async Task<AgentRun> GetRunRequiredAsync(ApprovalRequest approval, CancellationToken cancellationToken) =>
        await _runs.GetAsync(approval.TenantId, approval.RunId, cancellationToken).ConfigureAwait(false)
        ?? throw new DomainException(ErrorCategory.NotFound, "run.not_found", "The related run was not found.");

    private static void EnsurePending(ApprovalRequest approval)
    {
        if (approval.Status != ApprovalStatus.Pending)
        {
            throw new DomainException(ErrorCategory.Conflict, "approval.already_decided", "The approval request has already been decided.");
        }
    }

    private static void RequireApprovalPermission(UserContext user)
    {
        if (!user.Permissions.Contains("approvals.approve"))
        {
            throw new DomainException(ErrorCategory.Authorization, "approval.permission_denied", "The caller is not authorized to manage approvals.");
        }
    }

    private Task PublishAsync(AgentRun run, string type, ApprovalRequest approval, CancellationToken cancellationToken) =>
        _events.PublishAsync(new RunEvent(
            0,
            run.Id,
            run.TenantId,
            type,
            _clock.UtcNow,
            new Dictionary<string, string>
            {
                ["approval_id"] = approval.Id.ToString(),
                ["tool"] = approval.ToolName
            }), cancellationToken);

    private Task AppendAuditAsync(
        ApprovalRequest approval,
        UserContext actor,
        string action,
        string outcome,
        CancellationToken cancellationToken) =>
        _audit.AppendAsync(new AuditEntry(
            Guid.NewGuid(),
            approval.TenantId,
            actor.UserId,
            action,
            "approval",
            approval.Id.ToString(),
            outcome,
            _clock.UtcNow,
            actor.CorrelationId,
            new Dictionary<string, string>
            {
                ["run_id"] = approval.RunId.ToString(),
                ["tool"] = approval.ToolName
            }), cancellationToken);

    private static string FormatExecutionAnswer(string toolName, JsonElement output)
    {
        var identifier = output.TryGetProperty("ticketId", out var ticketId) ? ticketId.GetString() : null;
        return identifier is null
            ? $"The approved {toolName} operation completed successfully."
            : $"The approved support ticket was created successfully. Ticket ID: {identifier}.";
    }

    private static string? NormalizeReason(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            return null;
        }

        var normalized = reason.Trim();
        if (normalized.Length > 500)
        {
            throw new DomainException(ErrorCategory.Validation, "approval.reason_length", "The rejection reason is too long.");
        }

        return normalized;
    }

    private static string CreateToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
        .TrimEnd('=')
        .Replace('+', '-')
        .Replace('/', '_');

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static bool FixedTimeEquals(string left, string right) =>
        CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(left), Encoding.ASCII.GetBytes(right));
}
