using DotNet.Testcontainers.Builders;
using Microsoft.Extensions.Options;
using Orchestrator.Domain;
using Orchestrator.Infrastructure;
using StackExchange.Redis;

namespace Orchestrator.IntegrationTests;

public sealed class RedisIdempotencyStoreContainerTests
{
    [ContainerFact]
    public async Task RealRedisAtomicallyStoresTenantScopedIdempotencyKeys()
    {
        await using var redis = new ContainerBuilder("redis:8-alpine")
            .WithPortBinding(6379, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilCommandIsCompleted("redis-cli", "ping"))
            .Build();
        await redis.StartAsync(CancellationToken.None);

        var configuration = new ConfigurationOptions
        {
            AbortOnConnectFail = false
        };
        configuration.EndPoints.Add(redis.Hostname, redis.GetMappedPublicPort(6379));
        await using var connection = await ConnectionMultiplexer.ConnectAsync(configuration);
        var store = new RedisIdempotencyStore(connection, Options.Create(new RedisOptions
        {
            KeyPrefix = "eaokit-test"
        }));
        var tenant = TenantId.Create("tenant-alpha");
        var runId = RunId.New();

        await store.StoreAsync(tenant, "request-1", runId, DateTimeOffset.UtcNow.AddMinutes(5), CancellationToken.None);
        var loaded = await store.FindAsync(tenant, "request-1", CancellationToken.None);
        var crossTenant = await store.FindAsync(
            TenantId.Create("tenant-beta"),
            "request-1",
            CancellationToken.None);

        Assert.Equal(runId, loaded);
        Assert.Null(crossTenant);
        var duplicate = await Assert.ThrowsAsync<DomainException>(() => store.StoreAsync(
            tenant,
            "request-1",
            RunId.New(),
            DateTimeOffset.UtcNow.AddMinutes(5),
            CancellationToken.None));
        Assert.Equal("idempotency.conflict", duplicate.Code);
    }
}
