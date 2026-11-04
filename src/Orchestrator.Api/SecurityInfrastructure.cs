using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Orchestrator.Application;
using Orchestrator.Domain;

namespace Orchestrator.Api;

public enum AgentAuthenticationMode
{
    DevelopmentTokens,
    DevelopmentHeaders,
    Oidc
}

public sealed class AgentAuthenticationOptions
{
    public const string SectionName = "Authentication";

    public AgentAuthenticationMode Mode { get; set; } = AgentAuthenticationMode.DevelopmentTokens;
    public string? Authority { get; set; }
    public string Audience { get; set; } = "enterprise-agent-api";
    public bool RequireHttpsMetadata { get; set; } = true;
    public int DevelopmentTokenLifetimeMinutes { get; set; } = 60;
}

public static class AgentClaimTypes
{
    public const string TenantId = "tenant_id";
    public const string Permission = "permission";
    public const string AccessTag = "access_tag";
}

public static class DevelopmentIdentity
{
    public const string Scheme = "DevelopmentHeaders";
    public const string TenantHeader = "X-Tenant-Id";
    public const string UserHeader = "X-User-Id";
    public const string PermissionsHeader = "X-Permissions";
    public const string AccessTagsHeader = "X-Access-Tags";
    public const string DefaultTenant = "demo-tenant";
    public const string DefaultUser = "demo-user";
    public const string DefaultPermissions = "knowledge.read,documents.write,tickets.write,approvals.approve";
    public const string DefaultAccessTags = "employee";

    public static IEnumerable<string> ParseValues(string value) => value
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Distinct(StringComparer.OrdinalIgnoreCase);
}

public sealed class DevelopmentSigningCredentials
{
    public const string Issuer = "enterprise-agent-kit-development";

    public DevelopmentSigningCredentials()
    {
        var keyBytes = RandomNumberGenerator.GetBytes(64);
        Key = new SymmetricSecurityKey(keyBytes) { KeyId = Guid.NewGuid().ToString("N") };
        Credentials = new SigningCredentials(Key, SecurityAlgorithms.HmacSha256);
    }

    public SymmetricSecurityKey Key { get; }
    public SigningCredentials Credentials { get; }
}

public sealed class DevelopmentHeaderAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public DevelopmentHeaderAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : base(options, logger, encoder)
    {
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var tenant = Request.Headers[DevelopmentIdentity.TenantHeader].FirstOrDefault() ?? DevelopmentIdentity.DefaultTenant;
        var user = Request.Headers[DevelopmentIdentity.UserHeader].FirstOrDefault() ?? DevelopmentIdentity.DefaultUser;
        var permissions = Request.Headers[DevelopmentIdentity.PermissionsHeader].FirstOrDefault() ?? DevelopmentIdentity.DefaultPermissions;
        var accessTags = Request.Headers[DevelopmentIdentity.AccessTagsHeader].FirstOrDefault() ?? DevelopmentIdentity.DefaultAccessTags;

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user),
            new(ClaimTypes.NameIdentifier, user),
            new(AgentClaimTypes.TenantId, tenant)
        };
        claims.AddRange(DevelopmentIdentity.ParseValues(permissions).Select(value => new Claim(AgentClaimTypes.Permission, value)));
        claims.AddRange(DevelopmentIdentity.ParseValues(accessTags).Select(value => new Claim(AgentClaimTypes.AccessTag, value)));

        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name));
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name)));
    }
}

public static class AgentAuthenticationExtensions
{
    public static IServiceCollection AddAgentAuthentication(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        var settings = configuration.GetSection(AgentAuthenticationOptions.SectionName).Get<AgentAuthenticationOptions>()
            ?? new AgentAuthenticationOptions();
        ValidateSettings(settings, environment);

        services.Configure<AgentAuthenticationOptions>(configuration.GetSection(AgentAuthenticationOptions.SectionName));
        var signing = new DevelopmentSigningCredentials();
        services.AddSingleton(signing);

        var defaultScheme = settings.Mode == AgentAuthenticationMode.DevelopmentHeaders
            ? DevelopmentIdentity.Scheme
            : JwtBearerDefaults.AuthenticationScheme;

        var authentication = services.AddAuthentication(options =>
        {
            options.DefaultAuthenticateScheme = defaultScheme;
            options.DefaultChallengeScheme = defaultScheme;
        });

        authentication.AddScheme<AuthenticationSchemeOptions, DevelopmentHeaderAuthenticationHandler>(
            DevelopmentIdentity.Scheme,
            _ => { });
        authentication.AddJwtBearer(options =>
        {
            options.MapInboundClaims = false;
            options.RequireHttpsMetadata = settings.RequireHttpsMetadata;
            if (settings.Mode == AgentAuthenticationMode.Oidc)
            {
                options.Authority = settings.Authority;
                options.Audience = settings.Audience;
                return;
            }

            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuer = DevelopmentSigningCredentials.Issuer,
                ValidateAudience = true,
                ValidAudience = settings.Audience,
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = signing.Key,
                ValidateLifetime = true,
                ClockSkew = TimeSpan.FromSeconds(30),
                NameClaimType = JwtRegisteredClaimNames.Sub,
                RoleClaimType = ClaimTypes.Role
            };
        });

        services.AddAuthorizationBuilder()
            .SetFallbackPolicy(new AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .Build());
        return services;
    }

    private static void ValidateSettings(AgentAuthenticationOptions settings, IHostEnvironment environment)
    {
        if (!environment.IsDevelopment()
            && !environment.IsEnvironment("Testing")
            && settings.Mode != AgentAuthenticationMode.Oidc)
        {
            throw new InvalidOperationException("Authentication:Mode must be Oidc outside Development.");
        }

        if (settings.Mode == AgentAuthenticationMode.Oidc && string.IsNullOrWhiteSpace(settings.Authority))
        {
            throw new InvalidOperationException("Authentication:Authority is required in Oidc mode.");
        }

        if (string.IsNullOrWhiteSpace(settings.Audience))
        {
            throw new InvalidOperationException("Authentication:Audience is required.");
        }

        if (settings.DevelopmentTokenLifetimeMinutes is < 1 or > 1440)
        {
            throw new InvalidOperationException("Authentication:DevelopmentTokenLifetimeMinutes must be between 1 and 1440.");
        }
    }
}

public static class AuthenticatedUserContextFactory
{
    public static UserContext Create(HttpContext context)
    {
        if (context.User.Identity?.IsAuthenticated != true)
        {
            throw new DomainException(ErrorCategory.Authentication, "authentication.required", "An authenticated user is required.");
        }

        var tenant = context.User.FindFirstValue(AgentClaimTypes.TenantId);
        var user = context.User.FindFirstValue(JwtRegisteredClaimNames.Sub)
            ?? context.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(tenant) || string.IsNullOrWhiteSpace(user))
        {
            throw new DomainException(ErrorCategory.Authentication, "authentication.claims_missing", "Required identity claims are missing.");
        }

        var correlationId = context.Items[CorrelationIdMiddleware.HeaderName]?.ToString() ?? Guid.NewGuid().ToString("N");
        var traceId = System.Diagnostics.Activity.Current?.TraceId.ToString() ?? string.Empty;
        return new UserContext(
            TenantId.Create(tenant),
            UserId.Create(user),
            context.User.FindAll(AgentClaimTypes.Permission).Select(claim => claim.Value).ToHashSet(StringComparer.OrdinalIgnoreCase),
            context.User.FindAll(AgentClaimTypes.AccessTag).Select(claim => claim.Value).ToHashSet(StringComparer.OrdinalIgnoreCase),
            correlationId,
            traceId);
    }
}
