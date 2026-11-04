using Orchestrator.AgentFramework;
using Orchestrator.Application;
using Orchestrator.Domain;

namespace Orchestrator.UnitTests;

public sealed class MafAgentRuntimeTests
{
    [Fact]
    public async Task ExecuteAsyncFallsBackWhenModelSelectsUnknownAgent()
    {
        var registry = new AgentRegistry([new StubKnowledgeAgent()]);
        var runtime = new MafAgentRuntime(new UnknownAgentStructuredProvider(), registry);

        var result = await runtime.ExecuteAsync(CreateRequest(), CancellationToken.None);

        Assert.Equal("knowledge", result.AgentName);
        Assert.True(result.Answer.IsNoAnswer);
    }

    [Fact]
    public async Task ExecuteAsyncRunsMultipleAgentsInParallelAndCombinesTypedResults()
    {
        var first = new RecordingAgent("knowledge", "Knowledge result [1]", "document-a", "chunk-a");
        var second = new RecordingAgent("document-analyst", "Analysis result [1]", "document-b", "chunk-b");
        var registry = new AgentRegistry([first, second]);
        var runtime = new MafAgentRuntime(
            new FixedRouteStructuredProvider(new RouteDecision(
                ["knowledge", "document-analyst"],
                true,
                4,
                "multi_agent_test")),
            registry);

        var result = await runtime.ExecuteAsync(CreateRequest(), CancellationToken.None);

        Assert.Equal("supervisor", result.AgentName);
        Assert.Equal(2, result.Steps);
        Assert.Equal(2, result.Answer.Citations.Count);
        Assert.Contains("Knowledge result [1]", result.Answer.Text, StringComparison.Ordinal);
        Assert.Contains("Analysis result [2]", result.Answer.Text, StringComparison.Ordinal);
        Assert.True(first.Executed);
        Assert.True(second.Executed);
    }

    private static PreparedAgentRequest CreateRequest()
    {
        var user = new UserContext(
            TenantId.Create("demo-tenant"),
            UserId.Create("demo-user"),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "knowledge.read" },
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "employee" },
            "correlation-test",
            "trace-test");
        var original = new AgentRequest(
            ConversationId.New(),
            "What does the travel policy say?",
            user,
            "route-fallback-test");
        return new PreparedAgentRequest(
            original,
            original.Input,
            original.Input,
            [],
            new IntentDecision(IntentCategory.Knowledge, 0.99, "test"),
            InputRisk.None,
            new PolicyDecision(true, "test", new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "enterprise-search"
            }));
    }

    private sealed class UnknownAgentStructuredProvider : IStructuredCompletionProvider
    {
        public Task<StructuredCompletionResult<T>> CompleteAsync<T>(
            StructuredCompletionRequest<T> request,
            CancellationToken cancellationToken)
            where T : class
        {
            object decision = new RouteDecision(["unregistered-agent"], false, 4, "model_selection");
            return Task.FromResult(new StructuredCompletionResult<T>((T)decision, false, "test-model"));
        }
    }

    private sealed class FixedRouteStructuredProvider(RouteDecision decision) : IStructuredCompletionProvider
    {
        public Task<StructuredCompletionResult<T>> CompleteAsync<T>(
            StructuredCompletionRequest<T> request,
            CancellationToken cancellationToken)
            where T : class => Task.FromResult(new StructuredCompletionResult<T>((T)(object)decision, false, "test-model"));
    }

    private sealed class RecordingAgent(
        string name,
        string text,
        string documentId,
        string chunkId) : IAgentDefinition
    {
        public bool Executed { get; private set; }

        public AgentDescriptor Descriptor { get; } = new(
            name,
            $"Test {name} agent.",
            "1.0.0",
            new HashSet<IntentCategory> { IntentCategory.Knowledge, IntentCategory.DocumentAnalysis },
            2,
            TimeSpan.FromSeconds(1));

        public Task<AgentExecutionResult> ExecuteAsync(
            PreparedAgentRequest request,
            CancellationToken cancellationToken)
        {
            Executed = true;
            var citation = new Citation(
                "[1]",
                documentId,
                chunkId,
                name,
                $"demo://{documentId}",
                null,
                $"Evidence from {name}.");
            var answer = new AgentAnswer(text, [citation], true, false);
            var hit = new SearchHit(
                documentId,
                chunkId,
                name,
                citation.SourceUri,
                null,
                citation.Quote,
                1,
                request.Original.User.TenantId,
                []);
            return Task.FromResult(new AgentExecutionResult(name, answer, [hit], ["enterprise-search"], 1));
        }
    }

    private sealed class StubKnowledgeAgent : IAgentDefinition
    {
        public AgentDescriptor Descriptor { get; } = new(
            "knowledge",
            "Test knowledge agent.",
            "1.0.0",
            new HashSet<IntentCategory> { IntentCategory.Knowledge },
            4,
            TimeSpan.FromSeconds(1));

        public Task<AgentExecutionResult> ExecuteAsync(
            PreparedAgentRequest request,
            CancellationToken cancellationToken)
        {
            var answer = new AgentAnswer("No evidence.", [], false, true);
            return Task.FromResult(new AgentExecutionResult("knowledge", answer, [], [], 1));
        }
    }
}
