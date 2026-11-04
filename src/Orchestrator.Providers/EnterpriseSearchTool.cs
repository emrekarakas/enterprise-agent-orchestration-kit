using System.Text.Json;
using Orchestrator.Application;

namespace Orchestrator.Providers;

public sealed class EnterpriseSearchTool : IAgentTool
{
    private const double MinimumGroundingScore = 0.2;
    private readonly ISearchIndex _search;

    public EnterpriseSearchTool(ISearchIndex search) => _search = search;

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
            : MinimumGroundingScore;
        var hits = await _search.SearchAsync(new SearchRequest(
            context.Request.Original.User.TenantId,
            queryElement.GetString()!,
            context.Request.Original.User.AccessTags,
            top,
            minimumScore), cancellationToken).ConfigureAwait(false);

        return new ToolResult(true, JsonSerializer.SerializeToElement(new { hits }), null, false);
    }

    public bool ValidateResult(ToolResult result) =>
        result.Output.ValueKind == JsonValueKind.Object && result.Output.TryGetProperty("hits", out var hits) && hits.ValueKind == JsonValueKind.Array;
}
