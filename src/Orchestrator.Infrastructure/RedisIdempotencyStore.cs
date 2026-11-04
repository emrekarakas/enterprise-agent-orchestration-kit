using Microsoft.Extensions.Options;
using Orchestrator.Application;
using Orchestrator.Domain;
using StackExchange.Redis;

namespace Orchestrator.Infrastructure;

public sealed class RedisOptions
{
    public const string SectionName = "Redis";

    public string ConnectionString { get; set; } = "localhost:6379,abortConnect=false";
    public string KeyPrefix { get; set; } = "eaokit";
}

public sealed class RedisIdempotencyStore : IIdempotencyStore
{
    private readonly IConnectionMultiplexer _connection;
    private readonly RedisOptions _options;

    public RedisIdempotencyStore(
        IConnectionMultiplexer connection,
        IOptions<RedisOptions> options)
    {
        _connection = connection;
        _options = options.Value;
    }

    public async Task<RunId?> FindAsync(
        TenantId tenantId,
        string key,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var value = await _connection.GetDatabase()
            .StringGetAsync(BuildKey(tenantId, key)).WaitAsync(cancellationToken).ConfigureAwait(false);
        return value.HasValue && Guid.TryParse(value.ToString(), out var runId)
            ? new RunId(runId)
            : null;
    }

    public async Task StoreAsync(
        TenantId tenantId,
        string key,
        RunId runId,
        DateTimeOffset expiresAt,
        CancellationToken cancellationToken)
    {
        var lifetime = expiresAt - DateTimeOffset.UtcNow;
        if (lifetime <= TimeSpan.Zero)
        {
            throw new DomainException(ErrorCategory.Validation, "idempotency.expired", "The idempotency expiry must be in the future.");
        }

        var stored = await _connection.GetDatabase().StringSetAsync(
                BuildKey(tenantId, key),
                runId.Value.ToString("D"),
                lifetime,
                When.NotExists)
            .WaitAsync(cancellationToken).ConfigureAwait(false);
        if (!stored)
        {
            throw new DomainException(ErrorCategory.Conflict, "idempotency.conflict", "The idempotency key is already in use.");
        }
    }

    private RedisKey BuildKey(TenantId tenantId, string key) =>
        $"{_options.KeyPrefix}:idempotency:{tenantId.Value}:{key}";
}
