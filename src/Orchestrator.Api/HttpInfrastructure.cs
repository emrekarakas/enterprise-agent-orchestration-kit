using System.Diagnostics;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Orchestrator.Application;
using Orchestrator.Domain;

namespace Orchestrator.Api;

public sealed class CorrelationIdMiddleware
{
    public const string HeaderName = "X-Correlation-Id";
    private readonly RequestDelegate _next;

    public CorrelationIdMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context)
    {
        if (context.Request.Path.StartsWithSegments("/health"))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        var supplied = context.Request.Headers[HeaderName].FirstOrDefault();
        var correlationId = IsValid(supplied) ? supplied! : Guid.NewGuid().ToString("N");
        context.Items[HeaderName] = correlationId;
        context.Response.Headers[HeaderName] = correlationId;
        Activity.Current?.SetTag("app.correlation_id", correlationId);
        await _next(context).ConfigureAwait(false);
    }

    private static bool IsValid(string? value) => value is { Length: > 0 and <= 64 } && value.All(character => char.IsLetterOrDigit(character) || character is '-' or '_');
}

public sealed partial class GlobalExceptionHandler : IExceptionHandler
{
    private readonly IProblemDetailsService _problemDetails;
    private readonly ILogger<GlobalExceptionHandler> _logger;

    public GlobalExceptionHandler(
        IProblemDetailsService problemDetails,
        ILogger<GlobalExceptionHandler> logger)
    {
        _problemDetails = problemDetails;
        _logger = logger;
    }

    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        var (status, code, title, detail) = exception switch
        {
            DomainException domain => Map(domain),
            OperationCanceledException => (499, "request.cancelled", "Request cancelled", "The request was cancelled."),
            _ => (StatusCodes.Status500InternalServerError, "internal.error", "Internal server error", "An unexpected error occurred.")
        };
        if (status >= StatusCodes.Status500InternalServerError)
        {
            LogServerFailure(_logger, code, exception);
        }
        else
        {
            LogRequestRejected(_logger, code, status);
        }

        httpContext.Response.StatusCode = status;
        return await _problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            Exception = exception,
            ProblemDetails = new ProblemDetails
            {
                Status = status,
                Title = title,
                Detail = detail,
                Type = $"https://errors.enterprise-agent-kit.dev/{code}",
                Extensions =
                {
                    ["code"] = code,
                    ["correlationId"] = httpContext.Items[CorrelationIdMiddleware.HeaderName]?.ToString()
                }
            }
        }).ConfigureAwait(false);
    }

    private static (int Status, string Code, string Title, string Detail) Map(DomainException exception) => exception.Category switch
    {
        ErrorCategory.Validation => (400, exception.Code, "Validation failed", exception.SafeMessage),
        ErrorCategory.Authentication => (401, exception.Code, "Authentication required", exception.SafeMessage),
        ErrorCategory.Authorization or ErrorCategory.PolicyDenied => (403, exception.Code, "Request denied", exception.SafeMessage),
        ErrorCategory.NotFound => (404, exception.Code, "Resource not found", exception.SafeMessage),
        ErrorCategory.Conflict => (409, exception.Code, "Request conflict", exception.SafeMessage),
        ErrorCategory.Timeout => (504, exception.Code, "Dependency timeout", exception.SafeMessage),
        ErrorCategory.ProviderUnavailable or ErrorCategory.ExternalDependency => (503, exception.Code, "Dependency unavailable", exception.SafeMessage),
        ErrorCategory.InvalidModelOutput => (502, exception.Code, "Invalid model output", exception.SafeMessage),
        _ => (500, exception.Code, "Internal server error", exception.SafeMessage)
    };

    [LoggerMessage(1001, LogLevel.Error, "Request failed with error code {ErrorCode}.")]
    private static partial void LogServerFailure(
        ILogger logger,
        string errorCode,
        Exception exception);

    [LoggerMessage(1002, LogLevel.Warning, "Request rejected with error code {ErrorCode} and status {StatusCode}.")]
    private static partial void LogRequestRejected(
        ILogger logger,
        string errorCode,
        int statusCode);
}

public static class HttpContextExtensions
{
    public static UserContext GetUserContext(this HttpContext context) => AuthenticatedUserContextFactory.Create(context);
}
