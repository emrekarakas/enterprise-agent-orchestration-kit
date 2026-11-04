using System.Security.Cryptography;
using System.Text;
using Orchestrator.Application;

namespace Orchestrator.Providers;

public sealed class DeterministicFakeAiProvider :
    IChatCompletionProvider,
    IStructuredCompletionProvider,
    IEmbeddingProvider,
    IRerankingProvider,
    ISafetyProvider,
    ITokenCounter
{
    public Task<ChatCompletionResult> CompleteAsync(ChatCompletionRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var response = request.Operation switch
        {
            "answer.grounded" => $"The requested information is supported by the retrieved enterprise sources. {request.Input}",
            "answer.document_analysis" => $"The requested document analysis is supported by the retrieved enterprise sources. {request.Input}",
            _ => "The deterministic local provider completed the request."
        };
        return Task.FromResult(new ChatCompletionResult(response, Count(request.Input), Count(response), "deterministic-fake-v1"));
    }

    public Task<StructuredCompletionResult<T>> CompleteAsync<T>(
        StructuredCompletionRequest<T> request,
        CancellationToken cancellationToken)
        where T : class
    {
        cancellationToken.ThrowIfCancellationRequested();
        object result = request.DeterministicFallback;
        if (typeof(T) == typeof(IntentDecision))
        {
            result = Classify(request.Input);
        }
        else if (typeof(T) == typeof(RouteDecision))
        {
            result = Route(request.Input);
        }

        return Task.FromResult(new StructuredCompletionResult<T>((T)result, false, "deterministic-fake-v1"));
    }

    public Task<EmbeddingResult> EmbedAsync(string input, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input.Normalize(NormalizationForm.FormKC)));
        var vector = bytes.Select(value => (value - 127.5f) / 127.5f).ToArray();
        return Task.FromResult(new EmbeddingResult(vector, Count(input), "deterministic-hash-v1"));
    }

    public Task<IReadOnlyList<SearchHit>> RerankAsync(
        string query,
        IReadOnlyList<SearchHit> hits,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<SearchHit>>(hits.OrderByDescending(hit => hit.Score).ToArray());
    }

    public Task<SafetyResult> EvaluateAsync(string input, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var blocked = input.Contains("exfiltrate secrets", StringComparison.OrdinalIgnoreCase);
        return Task.FromResult(new SafetyResult(!blocked, blocked ? "secret_exfiltration" : "none", blocked ? 0.99 : 1.0));
    }

    public int Count(string input) => string.IsNullOrWhiteSpace(input)
        ? 0
        : input.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length;

    private static IntentDecision Classify(string input)
    {
        var normalized = input.ToLowerInvariant();
        if (normalized.Contains("ticket", StringComparison.Ordinal)
            || normalized.Contains("support request", StringComparison.Ordinal))
        {
            return new IntentDecision(IntentCategory.Workflow, 0.98, "workflow_keyword");
        }

        if (normalized.Contains("compare", StringComparison.Ordinal)
            || normalized.Contains("summarize", StringComparison.Ordinal))
        {
            return new IntentDecision(IntentCategory.DocumentAnalysis, 0.96, "document_analysis_keyword");
        }

        if (normalized is "hello" or "hi" or "thanks")
        {
            return new IntentDecision(IntentCategory.Conversation, 0.99, "conversation_keyword");
        }

        return new IntentDecision(IntentCategory.Knowledge, 0.95, "knowledge_default");
    }

    private static RouteDecision Route(string input)
    {
        var intent = Classify(input).Intent;
        var agents = intent switch
        {
            IntentCategory.DocumentAnalysis => new[] { "knowledge", "document-analyst" },
            IntentCategory.Workflow => new[] { "workflow" },
            IntentCategory.Conversation => new[] { "supervisor" },
            _ => new[] { "knowledge" }
        };
        return new RouteDecision(
            agents,
            intent == IntentCategory.DocumentAnalysis,
            4,
            $"intent_{intent.ToString().ToLowerInvariant()}");
    }
}
