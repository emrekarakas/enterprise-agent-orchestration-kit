using System.Text.Json;
using Orchestrator.Application;
using Orchestrator.Domain;
using Orchestrator.Providers;

namespace Orchestrator.UnitTests;

public sealed class ToolAndCitationTests
{
    [Fact]
    public void ToolRegistryRejectsDuplicateNames()
    {
        var search = new InMemoryEnterpriseSearch();
        var first = new EnterpriseSearchTool(search);
        var second = new EnterpriseSearchTool(search);

        Assert.Throws<InvalidOperationException>(() => new ToolRegistry([first, second]));
    }

    [Fact]
    public async Task SearchToolAppliesTenantIsolation()
    {
        var search = new InMemoryEnterpriseSearch();
        var result = await search.SearchAsync(new SearchRequest(
            TenantId.Create("demo-tenant"),
            "What is the international travel daily expense limit?",
            new HashSet<string> { "employee" },
            10,
            0.2), CancellationToken.None);

        Assert.NotEmpty(result);
        Assert.DoesNotContain(result, hit => hit.TenantId == TenantId.Create("other-tenant"));
    }

    [Fact]
    public void CitationValidatorRejectsAnswerWithCitationNotBackedByEvidence()
    {
        var hit = new SearchHit(
            "document-1",
            "chunk-1",
            "Title",
            "demo://document-1",
            "Section 1",
            "Verified evidence text.",
            1,
            TenantId.Create("demo-tenant"),
            []);
        var answer = new AgentAnswer(
            "Answer [1] [2]",
            [
                new Citation("[1]", "document-1", "chunk-1", "Title", "demo://document-1", "Section 1", "Verified evidence text."),
                new Citation("[2]", "document-2", "chunk-2", "Other", "demo://document-2", null, "Invented quote")
            ],
            true,
            false);

        var validated = CitationValidator.Validate(answer, [hit]);

        Assert.Empty(validated.Citations);
        Assert.False(validated.IsGrounded);
        Assert.True(validated.IsNoAnswer);
    }

    [Fact]
    public void CitationValidatorUsesDocumentAndChunkAsCompositeIdentity()
    {
        var first = new SearchHit(
            "document-1", "shared-chunk", "First", "demo://first", null,
            "First verified evidence.", 1, TenantId.Create("demo-tenant"), []);
        var second = new SearchHit(
            "document-2", "shared-chunk", "Second", "demo://second", null,
            "Second verified evidence.", 0.9, TenantId.Create("demo-tenant"), []);
        var answer = new AgentAnswer(
            "Grounded answer [1] [2]",
            [
                new Citation("[1]", "document-1", "shared-chunk", "First", "demo://first", null, "First verified evidence."),
                new Citation("[2]", "document-2", "shared-chunk", "Second", "demo://second", null, "Second verified evidence.")
            ],
            true,
            false);

        var validated = CitationValidator.Validate(answer, [first, second]);

        Assert.Equal(2, validated.Citations.Count);
        Assert.True(validated.IsGrounded);
    }

    [Fact]
    public async Task ToolDispatcherRejectsToolOutsidePreparedAllowList()
    {
        var tool = new EnterpriseSearchTool(new InMemoryEnterpriseSearch());
        var dispatcher = new ToolDispatcher(new ToolRegistry([tool]));
        var request = CreatePreparedRequest(new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        var exception = await Assert.ThrowsAsync<DomainException>(() => dispatcher.DispatchAsync(
            "enterprise-search",
            new ToolExecutionContext(request, "invocation", null),
            JsonSerializer.SerializeToElement(new { query = "travel" }),
            CancellationToken.None));

        Assert.Equal("tool.not_allowed", exception.Code);
    }

    private static PreparedAgentRequest CreatePreparedRequest(IReadOnlySet<string> allowedTools)
    {
        var user = new UserContext(
            TenantId.Create("demo-tenant"),
            UserId.Create("demo-user"),
            new HashSet<string> { "knowledge.read" },
            new HashSet<string> { "employee" },
            "correlation",
            "trace");
        var original = new AgentRequest(ConversationId.New(), "travel", user, "idempotency");
        return new PreparedAgentRequest(
            original,
            "travel",
            "travel",
            [],
            new IntentDecision(IntentCategory.Knowledge, 1, "test"),
            InputRisk.None,
            new PolicyDecision(true, "test", allowedTools));
    }
}
