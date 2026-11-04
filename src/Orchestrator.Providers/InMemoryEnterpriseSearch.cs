using System.Text.RegularExpressions;
using Orchestrator.Application;
using Orchestrator.Domain;

namespace Orchestrator.Providers;

public sealed class InMemoryEnterpriseSearch : ISearchIndex
{
    private static readonly Regex Tokens = new(@"[\p{L}\p{N}]+", RegexOptions.NonBacktracking);
    private readonly IReadOnlyList<SearchHit> _documents;

    public InMemoryEnterpriseSearch() => _documents = Seed().ToArray();

    public Task<IReadOnlyList<SearchHit>> SearchAsync(SearchRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var queryTokens = Tokenize(request.Query);
        var results = _documents
            .Where(hit => hit.TenantId == request.TenantId)
            .Where(hit => hit.AccessTags.Count == 0 || hit.AccessTags.Any(request.AccessTags.Contains))
            .Select(hit => hit with { Score = Score(queryTokens, Tokenize($"{hit.Title} {hit.Content}")) })
            .Where(hit => hit.Score >= request.MinimumScore)
            .OrderByDescending(hit => hit.Score)
            .ThenBy(hit => hit.ChunkId, StringComparer.Ordinal)
            .Take(request.Top)
            .ToArray();
        return Task.FromResult<IReadOnlyList<SearchHit>>(results);
    }

    private static HashSet<string> Tokenize(string input) => Tokens.Matches(input.ToLowerInvariant())
        .Select(match => match.Value)
        .Where(token => token.Length > 1)
        .ToHashSet(StringComparer.Ordinal);

    private static double Score(HashSet<string> query, HashSet<string> document)
    {
        if (query.Count == 0)
        {
            return 0;
        }

        var matches = query.Count(document.Contains);
        return matches / (double)query.Count;
    }

    private static IEnumerable<SearchHit> Seed()
    {
        var tenant = TenantId.Create("demo-tenant");
        var accessTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "employee" };
        yield return new SearchHit(
            "travel-policy",
            "travel-policy:international:1",
            "Synthetic Travel Policy",
            "demo://policies/travel",
            "International travel / Daily expenses",
            "The daily meal and incidental expense limit for international travel is 85 units. Hotel costs require receipts and are handled separately.",
            0,
            tenant,
            accessTags.ToArray());
        yield return new SearchHit(
            "security-policy",
            "security-policy:reporting:1",
            "Synthetic Information Security Policy",
            "demo://policies/security",
            "Incident reporting",
            "Suspected security incidents must be reported to the support desk within one hour of discovery. Employees must not investigate suspected incidents on their own.",
            0,
            tenant,
            accessTags.ToArray());
        yield return new SearchHit(
            "procurement-procedure",
            "procurement-procedure:approvals:1",
            "Synthetic Procurement Procedure",
            "demo://procedures/procurement",
            "Approval matrix",
            "Purchases above 5,000 units require approval from both the requesting department manager and procurement.",
            0,
            tenant,
            accessTags.ToArray());

        yield return new SearchHit(
            "isolated-policy",
            "isolated-policy:1",
            "Another Tenant Policy",
            "demo://other-tenant/private",
            "Private",
            "This content must never be returned to the demo tenant.",
            0,
            TenantId.Create("other-tenant"),
            accessTags.ToArray());
    }
}
