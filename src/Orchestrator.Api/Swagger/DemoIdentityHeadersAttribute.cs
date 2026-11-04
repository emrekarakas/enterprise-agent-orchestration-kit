using System.Reflection;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace Orchestrator.Api.Swagger;

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class DemoIdentityHeadersAttribute : Attribute;

public sealed class DemoIdentityHeadersOperationFilter : IOperationFilter
{
    private readonly AgentAuthenticationOptions _authentication;

    public DemoIdentityHeadersOperationFilter(IOptions<AgentAuthenticationOptions> authentication) =>
        _authentication = authentication.Value;

    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        var controllerType = context.MethodInfo.DeclaringType;
        if (controllerType is null
            || (!context.MethodInfo.IsDefined(typeof(DemoIdentityHeadersAttribute), true)
                && !controllerType.IsDefined(typeof(DemoIdentityHeadersAttribute), true)))
        {
            return;
        }

        if (_authentication.Mode != AgentAuthenticationMode.DevelopmentHeaders)
        {
            return;
        }

        operation.Parameters ??= [];
        AddHeader(operation, DevelopmentIdentity.TenantHeader, DevelopmentIdentity.DefaultTenant, "Tenant boundary.");
        AddHeader(operation, DevelopmentIdentity.UserHeader, DevelopmentIdentity.DefaultUser, "User identity.");
        AddHeader(operation, DevelopmentIdentity.PermissionsHeader, DevelopmentIdentity.DefaultPermissions, "Comma-separated permissions.");
        AddHeader(operation, DevelopmentIdentity.AccessTagsHeader, DevelopmentIdentity.DefaultAccessTags, "Comma-separated document access tags.");

        var idempotencyParameter = operation.Parameters.FirstOrDefault(parameter =>
            string.Equals(parameter.Name, "Idempotency-Key", StringComparison.OrdinalIgnoreCase));
        if (idempotencyParameter is not null)
        {
            idempotencyParameter.Description = "Optional in Swagger UI: when empty, the UI generates a unique key for each request. Required for non-Swagger clients.";
        }
    }

    private static void AddHeader(OpenApiOperation operation, string name, string defaultValue, string description)
    {
        var parameters = operation.Parameters ??= [];
        if (parameters.Any(parameter => string.Equals(parameter.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        parameters.Add(new OpenApiParameter
        {
            Name = name,
            In = ParameterLocation.Header,
            Required = false,
            Description = $"{description} Development default: {defaultValue}.",
            Example = JsonValue.Create(defaultValue),
            Schema = new OpenApiSchema
            {
                Type = JsonSchemaType.String,
                Default = JsonValue.Create(defaultValue)
            }
        });
    }
}
