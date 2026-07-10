using ResearchDiscovery.Application.Dtos;
using ResearchDiscovery.Application.Options;

namespace ResearchDiscovery.Application.Abstractions;

public sealed record SearchHit(
    PaperDto Paper,
    float MatchScore,
    bool IsWildcard,
    string? ExperienceProximity);

public sealed record SearchResult(
    SearchPlan Plan,
    IReadOnlyList<SearchHit> Hits,
    int TotalCandidates);

/// <summary>
/// Executes a SearchPlan: deterministic filters → embedding rank → wildcard
/// injection. Zero LLM calls. Exploration guardrails live here: experience
/// similarity only annotates (never ranks or gates), and wildcard slots are
/// sampled from outside the experience cluster.
/// </summary>
public interface ISearchService
{
    /// <param name="weights">Optional ranking-blend override, used only by the
    /// offline tuner; null means the configured (default: pure similarity) blend.</param>
    Task<SearchResult> SearchAsync(
        SearchPlan plan, int limit, CancellationToken ct, RankingWeights? weights = null);
}
