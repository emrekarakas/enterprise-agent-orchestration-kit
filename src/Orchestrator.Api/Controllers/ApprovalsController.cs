using Microsoft.AspNetCore.Mvc;
using Orchestrator.Api.Swagger;
using Orchestrator.Application;
using Orchestrator.Contracts;
using Orchestrator.Domain;

namespace Orchestrator.Api.Controllers;

[ApiController]
[Route("api/v1/approvals")]
[DemoIdentityHeaders]
public sealed class ApprovalsController : ControllerBase
{
    private readonly ApprovalService _approvals;

    public ApprovalsController(ApprovalService approvals) => _approvals = approvals;

    /// <summary>List unexpired pending approvals in the authenticated tenant.</summary>
    [HttpGet]
    [ProducesResponseType<ApprovalResponse[]>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<IReadOnlyList<ApprovalResponse>>> ListPending(CancellationToken cancellationToken)
    {
        var approvals = await _approvals.ListPendingAsync(
            HttpContext.GetUserContext(),
            cancellationToken).ConfigureAwait(false);
        return Ok(approvals.Select(item => item.ToResponse()).ToArray());
    }

    /// <summary>Approve and execute an immutable state-changing tool proposal.</summary>
    /// <remarks>The approval token is short-lived and single-use. Parameters cannot be supplied or changed here.</remarks>
    [HttpPost("{approvalId:guid}/approve")]
    [ProducesResponseType<ApprovalDecisionResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ApprovalDecisionResponse>> Approve(
        [FromRoute] Guid approvalId,
        [FromBody] ApproveApprovalRequest request,
        CancellationToken cancellationToken)
    {
        var outcome = await _approvals.ApproveAsync(
            HttpContext.GetUserContext(),
            new ApprovalId(approvalId),
            request.ApprovalToken,
            cancellationToken).ConfigureAwait(false);
        return Ok(new ApprovalDecisionResponse(
            outcome.Approval.ToResponse(),
            new AgentRunOutcome(outcome.Run, false).ToResponse()));
    }

    /// <summary>Reject a state-changing tool proposal without executing it.</summary>
    [HttpPost("{approvalId:guid}/reject")]
    [ProducesResponseType<ApprovalDecisionResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ApprovalDecisionResponse>> Reject(
        [FromRoute] Guid approvalId,
        [FromBody] RejectApprovalRequest request,
        CancellationToken cancellationToken)
    {
        var outcome = await _approvals.RejectAsync(
            HttpContext.GetUserContext(),
            new ApprovalId(approvalId),
            request.Reason,
            cancellationToken).ConfigureAwait(false);
        return Ok(new ApprovalDecisionResponse(
            outcome.Approval.ToResponse(),
            new AgentRunOutcome(outcome.Run, false).ToResponse()));
    }
}
