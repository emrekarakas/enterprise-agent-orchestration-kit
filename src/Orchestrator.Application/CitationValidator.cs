using Orchestrator.Domain;

namespace Orchestrator.Application;

public static class CitationValidator
{
    public static AgentAnswer Validate(AgentAnswer answer, IReadOnlyList<SearchHit> evidence)
    {
        var allowed = evidence
            .GroupBy(hit => (hit.DocumentId, hit.ChunkId))
            .ToDictionary(group => group.Key, group => group.First());
        var validated = answer.Citations
            .Where(citation => allowed.TryGetValue((citation.DocumentId, citation.ChunkId), out var hit)
                && hit.Content.Contains(citation.Quote, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        var hasInvalidCitation = validated.Length != answer.Citations.Count;
        if (hasInvalidCitation || (!answer.IsNoAnswer && validated.Length == 0))
        {
            return new AgentAnswer(
                "I could not produce an answer with verifiable citations from the available evidence.",
                [],
                false,
                true);
        }

        return answer with { Citations = validated, IsGrounded = answer.IsNoAnswer || validated.Length > 0 };
    }
}
