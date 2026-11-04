using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Orchestrator.Infrastructure;
using Orchestrator.Providers;
using StackExchange.Redis;

namespace Orchestrator.Api;

public sealed class PostgresReadinessHealthCheck : IHealthCheck
{
    private readonly IServiceScopeFactory _scopeFactory;

    public PostgresReadinessHealthCheck(IServiceScopeFactory scopeFactory) => _scopeFactory = scopeFactory;

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
            return await dbContext.Database.CanConnectAsync(cancellationToken).ConfigureAwait(false)
                ? HealthCheckResult.Healthy("PostgreSQL accepted a connection.")
                : HealthCheckResult.Unhealthy("PostgreSQL did not accept a connection.");
        }
        catch (Exception exception)
        {
            return HealthCheckResult.Unhealthy("PostgreSQL readiness check failed.", exception);
        }
    }
}

public sealed class DocumentWorkerReadinessHealthCheck : IHealthCheck
{
    private readonly IHttpClientFactory _httpClientFactory;

    public DocumentWorkerReadinessHealthCheck(IHttpClientFactory httpClientFactory) =>
        _httpClientFactory = httpClientFactory;

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await _httpClientFactory.CreateClient("document-worker-readiness")
                .GetAsync("health/ready", cancellationToken).ConfigureAwait(false);
            return response.IsSuccessStatusCode
                ? HealthCheckResult.Healthy("Document worker is ready.")
                : HealthCheckResult.Unhealthy($"Document worker returned HTTP {(int)response.StatusCode}.");
        }
        catch (Exception exception)
        {
            return HealthCheckResult.Unhealthy("Document worker readiness check failed.", exception);
        }
    }
}

public sealed class AzureAiSearchReadinessHealthCheck : IHealthCheck
{
    private readonly IServiceScopeFactory _scopeFactory;

    public AzureAiSearchReadinessHealthCheck(IServiceScopeFactory scopeFactory) => _scopeFactory = scopeFactory;

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var search = scope.ServiceProvider.GetRequiredService<AzureAiSearchIndex>();
            await search.CheckHealthAsync(cancellationToken).ConfigureAwait(false);
            return HealthCheckResult.Healthy("Azure AI Search index is reachable.");
        }
        catch (Exception exception)
        {
            return HealthCheckResult.Unhealthy("Azure AI Search readiness check failed.", exception);
        }
    }
}

public sealed class RedisReadinessHealthCheck : IHealthCheck
{
    private readonly IConnectionMultiplexer _connection;

    public RedisReadinessHealthCheck(IConnectionMultiplexer connection) => _connection = connection;

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var latency = await _connection.GetDatabase().PingAsync()
                .WaitAsync(cancellationToken).ConfigureAwait(false);
            return HealthCheckResult.Healthy(
                "Redis accepted a PING.",
                new Dictionary<string, object> { ["latencyMilliseconds"] = latency.TotalMilliseconds });
        }
        catch (Exception exception)
        {
            return HealthCheckResult.Unhealthy("Redis readiness check failed.", exception);
        }
    }
}
