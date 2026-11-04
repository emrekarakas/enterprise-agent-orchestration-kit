using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Orchestrator.Api.Swagger;
using Orchestrator.Application;
using Orchestrator.Contracts;
using Orchestrator.Domain;

namespace Orchestrator.Api.Controllers;

[ApiController]
[Route("api/v1/runs")]
[DemoIdentityHeaders]
public sealed class AgentRunsController : ControllerBase
{
    private readonly IRunStore _runs;
    private readonly IRunEventBroker _events;
    private readonly AgentRunService _runService;
    private readonly FeedbackService _feedback;

    public AgentRunsController(
        IRunStore runs,
        IRunEventBroker events,
        AgentRunService runService,
        FeedbackService feedback)
    {
        _runs = runs;
        _events = events;
        _runService = runService;
        _feedback = feedback;
    }

    /// <summary>Cancel a non-terminal tenant-scoped run.</summary>
    [HttpPost("{runId:guid}/cancel")]
    [ProducesResponseType<AgentRunResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<AgentRunResponse>> Cancel(
        [FromRoute] Guid runId,
        CancellationToken cancellationToken)
    {
        var run = await _runService.CancelAsync(
            HttpContext.GetUserContext(),
            new RunId(runId),
            cancellationToken).ConfigureAwait(false);
        return Ok(new AgentRunOutcome(run, false).ToResponse());
    }

    /// <summary>Record one immutable feedback item for a completed user-owned run.</summary>
    [HttpPost("{runId:guid}/feedback")]
    [ProducesResponseType<FeedbackResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<FeedbackResponse>> SubmitFeedback(
        [FromRoute] Guid runId,
        [FromBody] SubmitFeedbackRequest request,
        CancellationToken cancellationToken)
    {
        var feedback = await _feedback.CreateAsync(
            HttpContext.GetUserContext(),
            new RunId(runId),
            request.Rating,
            request.Comment,
            cancellationToken).ConfigureAwait(false);
        var response = new FeedbackResponse(
            feedback.Id, feedback.RunId.Value, feedback.Rating, feedback.Comment, feedback.CreatedAt);
        return Created($"/api/v1/runs/{runId}/feedback", response);
    }

    /// <summary>Get a tenant-scoped agent run.</summary>
    [HttpGet("{runId:guid}")]
    [ProducesResponseType<AgentRunResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<AgentRunResponse>> Get(
        [FromRoute] Guid runId,
        CancellationToken cancellationToken)
    {
        var user = HttpContext.GetUserContext();
        var run = await _runs.GetAsync(user.TenantId, new RunId(runId), cancellationToken).ConfigureAwait(false);
        return run is null ? NotFound() : Ok(new AgentRunOutcome(run, false).ToResponse());
    }

    /// <summary>Stream recorded run events using Server-Sent Events.</summary>
    /// <remarks>Use an SSE-capable client or <c>curl -N</c>. The optional after value is the last observed sequence.</remarks>
    [HttpGet("{runId:guid}/events")]
    [Produces("text/event-stream")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task StreamEvents(
        [FromRoute] Guid runId,
        [FromQuery] long? after,
        CancellationToken cancellationToken)
    {
        Response.ContentType = "text/event-stream";
        Response.Headers.CacheControl = "no-cache";
        var tenantId = HttpContext.GetUserContext().TenantId;

        await foreach (var runEvent in _events.ReadAsync(tenantId, new RunId(runId), after ?? 0, cancellationToken))
        {
            var data = JsonSerializer.Serialize(new
            {
                sequence = runEvent.Sequence,
                runId = runEvent.RunId.Value,
                type = runEvent.Type,
                occurredAt = runEvent.OccurredAt,
                data = runEvent.Data
            });
            await Response.WriteAsync($"id: {runEvent.Sequence.ToString(CultureInfo.InvariantCulture)}\n", cancellationToken).ConfigureAwait(false);
            await Response.WriteAsync($"event: {runEvent.Type}\n", cancellationToken).ConfigureAwait(false);
            await Response.WriteAsync($"data: {data}\n\n", cancellationToken).ConfigureAwait(false);
            await Response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
