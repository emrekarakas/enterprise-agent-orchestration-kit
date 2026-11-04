using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Orchestrator.Contracts;

namespace Orchestrator.Api.Controllers;

[ApiController]
[Route("api/v1/auth")]
public sealed class AuthenticationController : ControllerBase
{
    private readonly AgentAuthenticationOptions _options;
    private readonly DevelopmentSigningCredentials _signing;
    private readonly IHostEnvironment _environment;

    public AuthenticationController(
        IOptions<AgentAuthenticationOptions> options,
        DevelopmentSigningCredentials signing,
        IHostEnvironment environment)
    {
        _options = options.Value;
        _signing = signing;
        _environment = environment;
    }

    /// <summary>Create a short-lived JWT for the synthetic local demo identity.</summary>
    /// <remarks>This endpoint exists only in Development and is not a username/password authentication system.</remarks>
    [AllowAnonymous]
    [HttpPost("development-token")]
    [ProducesResponseType<DevelopmentTokenResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<DevelopmentTokenResponse> CreateDevelopmentToken([FromBody] DevelopmentTokenRequest? request)
    {
        if ((!_environment.IsDevelopment() && !_environment.IsEnvironment("Testing"))
            || _options.Mode != AgentAuthenticationMode.DevelopmentTokens)
        {
            return NotFound();
        }

        request ??= new DevelopmentTokenRequest(null, null, null, null);
        var tenant = string.IsNullOrWhiteSpace(request.TenantId) ? DevelopmentIdentity.DefaultTenant : request.TenantId.Trim();
        var user = string.IsNullOrWhiteSpace(request.UserId) ? DevelopmentIdentity.DefaultUser : request.UserId.Trim();
        var permissions = request.Permissions is { Count: > 0 }
            ? request.Permissions
            : DevelopmentIdentity.ParseValues(DevelopmentIdentity.DefaultPermissions).ToArray();
        var accessTags = request.AccessTags is { Count: > 0 }
            ? request.AccessTags
            : DevelopmentIdentity.ParseValues(DevelopmentIdentity.DefaultAccessTags).ToArray();

        var now = DateTimeOffset.UtcNow;
        var expiresAt = now.AddMinutes(_options.DevelopmentTokenLifetimeMinutes);
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")),
            new(AgentClaimTypes.TenantId, tenant)
        };
        claims.AddRange(permissions.Distinct(StringComparer.OrdinalIgnoreCase).Select(value => new Claim(AgentClaimTypes.Permission, value)));
        claims.AddRange(accessTags.Distinct(StringComparer.OrdinalIgnoreCase).Select(value => new Claim(AgentClaimTypes.AccessTag, value)));

        var encoded = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = DevelopmentSigningCredentials.Issuer,
            Audience = _options.Audience,
            Subject = new ClaimsIdentity(claims),
            NotBefore = now.UtcDateTime,
            Expires = expiresAt.UtcDateTime,
            SigningCredentials = _signing.Credentials
        });
        return Ok(new DevelopmentTokenResponse(encoded, "Bearer", expiresAt));
    }
}
