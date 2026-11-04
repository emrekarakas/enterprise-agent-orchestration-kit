using Microsoft.AspNetCore.Mvc;
using Orchestrator.Api.Swagger;
using Orchestrator.Application;
using Orchestrator.Contracts;

namespace Orchestrator.Api.Controllers;

[ApiController]
[Route("api/v1/catalog")]
[DemoIdentityHeaders]
public sealed class CatalogController : ControllerBase
{
    private readonly AgentRegistry _agents;
    private readonly ToolRegistry _tools;

    public CatalogController(AgentRegistry agents, ToolRegistry tools)
    {
        _agents = agents;
        _tools = tools;
    }

    /// <summary>List immutable descriptors for all registered agents.</summary>
    [HttpGet("agents")]
    [ProducesResponseType<AgentCatalogItemResponse[]>(StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyList<AgentCatalogItemResponse>> ListAgents() => Ok(
        _agents.List().Select(item => new AgentCatalogItemResponse(
            item.Name,
            item.Description,
            item.Version,
            item.SupportedIntents.Select(intent => intent.ToString()).ToArray(),
            item.MaxSteps,
            (int)item.Timeout.TotalSeconds)).ToArray());

    /// <summary>List schemas, permissions, risk, and execution policy for registered tools.</summary>
    [HttpGet("tools")]
    [ProducesResponseType<ToolCatalogItemResponse[]>(StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyList<ToolCatalogItemResponse>> ListTools() => Ok(
        _tools.List().Select(item => new ToolCatalogItemResponse(
            item.Name,
            item.Description,
            item.Version,
            item.InputSchema,
            item.OutputSchema,
            item.RequiredPermissions.Order(StringComparer.Ordinal).ToArray(),
            item.RiskLevel.ToString(),
            item.MutationBehavior.ToString(),
            (int)item.Timeout.TotalSeconds,
            item.MaxRetries,
            item.IdempotencyBehavior.ToString())).ToArray());
}
