using Microsoft.EntityFrameworkCore;
using Orchestrator.Domain;
using Orchestrator.Infrastructure;
using Testcontainers.PostgreSql;

namespace Orchestrator.IntegrationTests;

public sealed class PostgresPlatformStoreContainerTests
{
    [ContainerFact]
    public async Task RealPostgresPersistsDataAndEnforcesTenantScopedReads()
    {
        await using var postgres = new PostgreSqlBuilder("postgres:17-alpine")
            .WithDatabase("agentkit_test")
            .WithUsername("agentkit")
            .WithPassword("test-only-password")
            .Build();
        await postgres.StartAsync(CancellationToken.None);

        var options = new DbContextOptionsBuilder<OrchestratorDbContext>()
            .UseNpgsql(postgres.GetConnectionString())
            .Options;
        await using var dbContext = new OrchestratorDbContext(options);
        await dbContext.Database.MigrateAsync(CancellationToken.None);
        var store = new PostgresPlatformStore(dbContext);

        var tenant = TenantId.Create("tenant-alpha");
        var otherTenant = TenantId.Create("tenant-beta");
        var user = UserId.Create("integration-user");
        var now = DateTimeOffset.UtcNow;
        var conversation = new Conversation(
            ConversationId.New(),
            tenant,
            user,
            "PostgreSQL integration",
            now,
            now);

        await store.CreateAsync(conversation, CancellationToken.None);
        await store.AddMessageAsync(new ConversationMessage(
            MessageId.New(),
            conversation.Id,
            tenant,
            MessageRole.User,
            "Tenant-scoped persisted message",
            now.AddSeconds(1)), CancellationToken.None);

        var loaded = await store.GetAsync(tenant, conversation.Id, CancellationToken.None);
        var crossTenant = await store.GetAsync(otherTenant, conversation.Id, CancellationToken.None);
        var messages = await store.GetRecentMessagesAsync(
            tenant,
            conversation.Id,
            10,
            CancellationToken.None);
        var crossTenantMessages = await store.GetRecentMessagesAsync(
            otherTenant,
            conversation.Id,
            10,
            CancellationToken.None);

        Assert.NotNull(loaded);
        Assert.Equal(conversation.Id, loaded.Id);
        Assert.Null(crossTenant);
        Assert.Single(messages);
        Assert.Equal("Tenant-scoped persisted message", messages[0].Content);
        Assert.Empty(crossTenantMessages);
    }
}
