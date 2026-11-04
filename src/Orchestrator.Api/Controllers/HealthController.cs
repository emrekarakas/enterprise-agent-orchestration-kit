using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Orchestrator.Api.Controllers;

[ApiController]
[AllowAnonymous]
[Route("health")]
public sealed class HealthController : ControllerBase
{
    private readonly HealthCheckService _healthChecks;

    public HealthController(HealthCheckService healthChecks) => _healthChecks = healthChecks;

    /// <summary>Return whether the API process is alive.</summary>
    [HttpGet("live")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public IActionResult Live() => Ok(new { status = "Healthy" });

    /// <summary>Return whether configured dependencies are ready.</summary>
    [HttpGet("ready")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public Task<IActionResult> Ready(CancellationToken cancellationToken) => CheckAsync(cancellationToken);

    private async Task<IActionResult> CheckAsync(CancellationToken cancellationToken)
    {
        var report = await _healthChecks.CheckHealthAsync(
            registration => registration.Tags.Contains("ready"),
            cancellationToken).ConfigureAwait(false);
        var response = new
        {
            status = report.Status.ToString(),
            durationMilliseconds = report.TotalDuration.TotalMilliseconds,
            dependencies = report.Entries.ToDictionary(
                entry => entry.Key,
                entry => new
                {
                    status = entry.Value.Status.ToString(),
                    durationMilliseconds = entry.Value.Duration.TotalMilliseconds
                })
        };
        return report.Status == HealthStatus.Unhealthy
            ? StatusCode(StatusCodes.Status503ServiceUnavailable, response)
            : Ok(response);
    }
}
