using Microsoft.AspNetCore.Mvc;
using Orchestrator.Api.Swagger;
using Orchestrator.Application;
using Orchestrator.Contracts;
using Orchestrator.Domain;

namespace Orchestrator.Api.Controllers;

[ApiController]
[Route("api/v1/conversations")]
[DemoIdentityHeaders]
public sealed class ConversationsController : ControllerBase
{
    private readonly ConversationService _conversations;
    private readonly AgentRunService _runs;

    public ConversationsController(ConversationService conversations, AgentRunService runs)
    {
        _conversations = conversations;
        _runs = runs;
    }

    /// <summary>Create a tenant- and user-scoped conversation.</summary>
    /// <remarks>Development identity headers are optional and use documented demo defaults.</remarks>
    [HttpPost]
    [ProducesResponseType<ConversationResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ConversationResponse>> Create(
        [FromBody] CreateConversationRequest request,
        CancellationToken cancellationToken)
    {
        var conversation = await _conversations.CreateAsync(
            HttpContext.GetUserContext(),
            request.Title,
            cancellationToken).ConfigureAwait(false);
        var response = conversation.ToResponse();
        return Created($"/api/v1/conversations/{response.Id}", response);
    }

    /// <summary>List conversations for the current tenant and user.</summary>
    [HttpGet]
    [ProducesResponseType<ConversationResponse[]>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<ConversationResponse>>> List(CancellationToken cancellationToken)
    {
        var conversations = await _conversations.ListAsync(
            HttpContext.GetUserContext(),
            cancellationToken).ConfigureAwait(false);
        return Ok(conversations.Select(item => item.ToResponse()).ToArray());
    }

    /// <summary>Send a message and synchronously execute an agent run.</summary>
    /// <remarks>
    /// Executes preparation, MAF routing, authorized tools, grounded response generation,
    /// citation validation, audit, and memory update. Try the synthetic question:
    /// "What is the international travel daily expense limit?".
    /// </remarks>
    [HttpPost("{conversationId:guid}/messages")]
    [ProducesResponseType<AgentRunResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status502BadGateway)]
    public async Task<ActionResult<AgentRunResponse>> SendMessage(
        [FromRoute] Guid conversationId,
        [FromBody] SendMessageRequest request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            throw new DomainException(ErrorCategory.Validation, "idempotency.required", "Idempotency-Key header is required.");
        }

        var outcome = await _runs.ExecuteAsync(new AgentRequest(
            new ConversationId(conversationId),
            request.Message,
            HttpContext.GetUserContext(),
            idempotencyKey), cancellationToken).ConfigureAwait(false);
        return Ok(outcome.ToResponse());
    }
}
