using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ResearchDiscovery.Application.Abstractions;
using ResearchDiscovery.Infrastructure.Persistence;
using ResearchDiscovery.Infrastructure.Profile;

namespace ResearchDiscovery.Infrastructure.Search;

/// <summary>
/// Executes a SearchPlan with zero LLM calls: SQL filters narrow the corpus,
/// the embedding index ranks by relevance to the plan's anchor text, and
/// wildcard slots inject high-relevance papers from OUTSIDE the user's
/// experience cluster.
///
/// Exploration guardrails (deliberate, load-bearing):
/// - Relevance is the ONLY ranking signal. Experience similarity never gates
///   or reorders results — it just annotates hits as "close to home"/"stretch".
/// - Wildcards are structural serendipity: the least experience-similar papers
///   from the high-relevance pool, so the user's comfort zone can't silently
///   narrow what they see.
/// </summary>
public class SearchService(
    AppDbContext db,
    ITextEmbedder embedder,
    IEmbeddingIndex index,
    IPaperQueryService queryService,
    ProfileService profileService,
    ILogger<SearchService> logger) : ISearchService
{
    private const int WildcardSlots = 2;
    private const int PoolMultiplier = 5;
    private const float CloseToHomeThreshold = 0.45f;
    private const float StretchThreshold = 0.25f;

    public async Task<SearchResult> SearchAsync(SearchPlan plan, int limit, CancellationToken ct)
    {
        limit = Math.Clamp(limit, 1, 200);

        // Stage 0: deterministic SQL filters.
        IQueryable<Domain.Entities.Paper> candidates = db.Papers.AsNoTracking();

        if (plan.Categories.Count > 0)
        {
            var codes = plan.Categories;
            candidates = candidates.Where(
                p => p.PaperCategories.Any(pc => codes.Contains(pc.Category.Code)));
        }

        if (plan.DateWindowDays is { } days and > 0)
        {
            var cutoff = DateTimeOffset.UtcNow.AddDays(-days);
            candidates = candidates.Where(p => p.PublishedUtc >= cutoff);
        }

        if (plan.RequireNoCode == true)
        {
            candidates = candidates.Where(p => p.CodeUrl == null);
        }

        var candidateIds = (await candidates.Select(p => p.Id).ToListAsync(ct)).ToHashSet();
        if (candidateIds.Count == 0)
        {
            return new SearchResult(plan, [], 0);
        }

        // Stage 1: embedding rank over the candidates (local model, no tokens).
        var queryVector = await embedder.EmbedAsync(plan.AnchorText, ct);
        var pool = await index.TopAsync(
            queryVector, Math.Max(limit * PoolMultiplier, limit + 50), candidateIds, ct);

        if (pool.Count == 0)
        {
            logger.LogWarning(
                "Search matched {Candidates} papers but none have embeddings — run `embed` first",
                candidateIds.Count);
            return new SearchResult(plan, [], candidateIds.Count);
        }

        // Experience annotation + wildcards need the profile's experience vector.
        var profile = await profileService.GetAsync(ct);
        IReadOnlyDictionary<long, float>? experienceScores = null;
        if (!string.IsNullOrWhiteSpace(profile?.ExperienceSummary))
        {
            var experienceVector = await embedder.EmbedAsync(profile!.ExperienceSummary, ct);
            experienceScores = await index.ScoreAsync(
                pool.Select(s => s.PaperId), experienceVector, ct);
        }

        var selection = SelectWithWildcards(pool, limit, experienceScores);

        var dtos = await queryService.GetPapersByIdsAsync(
            selection.Select(s => s.Paper.PaperId).ToList(), ct);

        var hits = selection
            .Where(s => dtos.ContainsKey(s.Paper.PaperId))
            .Select(s => new SearchHit(
                dtos[s.Paper.PaperId],
                s.Paper.Score,
                s.IsWildcard,
                Proximity(experienceScores, s.Paper.PaperId)))
            .ToList();

        return new SearchResult(plan, hits, candidateIds.Count);
    }

    /// <summary>
    /// Top papers by relevance, with the final slots reserved for wildcards:
    /// papers still in the high-relevance pool but least similar to the user's
    /// experience. Without a profile there is no experience cluster to escape,
    /// so the plain top-N is returned.
    /// </summary>
    internal static List<(ScoredPaper Paper, bool IsWildcard)> SelectWithWildcards(
        IReadOnlyList<ScoredPaper> pool,
        int limit,
        IReadOnlyDictionary<long, float>? experienceScores)
    {
        if (experienceScores is null || pool.Count <= limit || limit <= WildcardSlots)
        {
            return pool.Take(limit).Select(p => (p, false)).ToList();
        }

        var top = pool.Take(limit - WildcardSlots).ToList();
        var topIds = top.Select(t => t.PaperId).ToHashSet();

        var wildcards = pool
            .Skip(limit - WildcardSlots)
            .Where(p => !topIds.Contains(p.PaperId))
            .OrderBy(p => experienceScores.TryGetValue(p.PaperId, out var s) ? s : float.MaxValue)
            .Take(WildcardSlots)
            .ToList();

        var result = top.Select(p => (p, IsWildcard: false)).ToList();
        result.AddRange(wildcards.Select(p => (p, IsWildcard: true)));
        return result;
    }

    private static string? Proximity(
        IReadOnlyDictionary<long, float>? experienceScores, long paperId)
    {
        if (experienceScores is null || !experienceScores.TryGetValue(paperId, out var score))
        {
            return null;
        }

        return score switch
        {
            >= CloseToHomeThreshold => "close",
            < StretchThreshold => "stretch",
            _ => null,
        };
    }
}
