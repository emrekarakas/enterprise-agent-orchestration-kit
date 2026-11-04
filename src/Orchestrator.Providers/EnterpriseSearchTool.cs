using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using Orchestrator.Application;

namespace Orchestrator.Providers;

public sealed class RetrievalOptions
{
    public const string SectionName = "Retrieval";

    public double MinimumGroundingScore { get; set; } = 0.2;
    public bool RequireLexicalEvidenceOverlap { get; set; } = true;
}

public sealed class EnterpriseSearchTool : IAgentTool
{
    private static readonly Regex WordPattern = new(@"[\p{L}\p{N}]+", RegexOptions.Compiled);
    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "an", "and", "are", "as", "at", "be", "both", "by", "do", "does", "for", "from",
        "how", "in", "is", "it", "must", "of", "on", "or", "should", "the", "this", "to", "what",
        "when", "where", "who", "why", "will", "with", "describe", "compare", "state", "summarize"
    };
    private readonly ISearchIndex _search;
    private readonly RetrievalOptions _options;

    public EnterpriseSearchTool(ISearchIndex search, IOptions<RetrievalOptions> options)
    {
        _search = search;
        _options = options.Value;
        if (_options.MinimumGroundingScore is < 0 or > 1)
        {
            throw new InvalidOperationException("Retrieval:MinimumGroundingScore must be between 0 and 1.");
        }
    }

    public EnterpriseSearchTool(ISearchIndex search)
        : this(search, Options.Create(new RetrievalOptions()))
    {
    }

    public ToolDescriptor Descriptor { get; } = new(
        "enterprise-search",
        "Searches tenant-isolated enterprise knowledge sources.",
        "1.0.0",
        "{\"type\":\"object\",\"required\":[\"query\"],\"properties\":{\"query\":{\"type\":\"string\",\"minLength\":1,\"maxLength\":8000},\"top\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":10},\"minimumScore\":{\"type\":\"number\",\"minimum\":0,\"maximum\":1}},\"additionalProperties\":false}",
        "{\"type\":\"object\",\"required\":[\"hits\"],\"properties\":{\"hits\":{\"type\":\"array\"}},\"additionalProperties\":false}",
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "knowledge.read" },
        ToolRiskLevel.Low,
        ToolMutationBehavior.ReadOnly,
        TimeSpan.FromSeconds(5),
        1,
        ToolIdempotencyBehavior.NotApplicable);

    public bool ValidateArguments(JsonElement arguments) =>
        arguments.ValueKind == JsonValueKind.Object
        && arguments.TryGetProperty("query", out var query)
        && query.ValueKind == JsonValueKind.String
        && !string.IsNullOrWhiteSpace(query.GetString())
        && query.GetString()!.Length <= 8_000
        && (!arguments.TryGetProperty("top", out var top)
            || top.ValueKind == JsonValueKind.Number && top.TryGetInt32(out var topValue) && topValue is >= 1 and <= 10)
        && (!arguments.TryGetProperty("minimumScore", out var minimumScore)
            || minimumScore.ValueKind == JsonValueKind.Number
            && minimumScore.TryGetDouble(out var minimumScoreValue)
            && minimumScoreValue is >= 0 and <= 1);

    public async Task<ToolResult> ExecuteAsync(
        ToolExecutionContext context,
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        if (!arguments.TryGetProperty("query", out var queryElement)
            || string.IsNullOrWhiteSpace(queryElement.GetString()))
        {
            return new ToolResult(false, JsonSerializer.SerializeToElement(new { hits = Array.Empty<object>() }), "tool.arguments_invalid", false);
        }

        var top = arguments.TryGetProperty("top", out var topElement) ? topElement.GetInt32() : 5;
        var minimumScore = arguments.TryGetProperty("minimumScore", out var scoreElement)
            ? scoreElement.GetDouble()
            : _options.MinimumGroundingScore;
        var query = queryElement.GetString()!;
        var hits = await _search.SearchAsync(new SearchRequest(
            context.Request.Original.User.TenantId,
            query,
            context.Request.Original.User.AccessTags,
            top,
            minimumScore), cancellationToken).ConfigureAwait(false);
        if (_options.RequireLexicalEvidenceOverlap)
        {
            hits = hits.Where(hit => HasLexicalSupport(query, $"{hit.Title} {hit.Content}")).ToArray();
        }

        return new ToolResult(true, JsonSerializer.SerializeToElement(new { hits }), null, false);
    }

    public bool ValidateResult(ToolResult result) =>
        result.Output.ValueKind == JsonValueKind.Object && result.Output.TryGetProperty("hits", out var hits) && hits.ValueKind == JsonValueKind.Array;

    private static bool HasLexicalSupport(string query, string evidence)
    {
        var queryTerms = Tokenize(query);
        if (queryTerms.Count == 0)
        {
            return false;
        }

        var requiredMatches = Math.Min(2, queryTerms.Count);
        return Tokenize(evidence).Intersect(queryTerms).Take(requiredMatches).Count() == requiredMatches;
    }

    private static HashSet<string> Tokenize(string text) => WordPattern.Matches(text)
        .Select(match => match.Value.ToLowerInvariant())
        .Where(token => token.Length >= 3 && !StopWords.Contains(token))
        .ToHashSet(StringComparer.OrdinalIgnoreCase);
}
