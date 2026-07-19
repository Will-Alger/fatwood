using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ResearchDiscovery.Application.Abstractions;
using ResearchDiscovery.Application.Options;
using ResearchDiscovery.Infrastructure.Persistence;
using ResearchDiscovery.Infrastructure.Profile;

namespace ResearchDiscovery.Infrastructure.Search;

/// <summary>
/// Executes a SearchPlan with zero LLM calls through a staged pipeline —
/// every stage beyond stage 1 is config-flagged OFF until the eval harness
/// proves it beats the baseline:
///
///   Stage 0: SQL filters narrow the corpus (categories/date/code).
///   Stage 1: dense retrieval — single-anchor cosine, or per-topic max-sim
///            (UseMultiAnchor) so multi-topic queries aren't averaged away.
///   Stage 1b: BM25 lexical retrieval fused via Reciprocal Rank Fusion
///            (UseHybrid) — exact terminology embeddings blur.
///   Stage 2: signal blend (recency / code / citations; default pure cosine).
///   Stage 3: cross-encoder rerank of the pool head (UseReranker).
///
/// Exploration guardrails (deliberate, load-bearing):
/// - Relevance is the ONLY ranking signal. Experience similarity never gates
///   or reorders results — it just annotates hits as "close to home"/"stretch".
/// - Wildcards are structural serendipity: the least experience-similar papers
///   from the high-relevance pool, so the user's comfort zone can't silently
///   narrow what they see.
///
/// Interleaving: when Ranking:InterleaveCandidate is on, PRODUCT searches
/// (never eval runs) team-draft two rankers' results and tag each slot A/B,
/// so real interactions arbitrate between them.
/// </summary>
public class SearchService(
    AppDbContext db,
    ITextEmbedder embedder,
    IEmbeddingIndex index,
    ILexicalIndex lexicalIndex,
    ICrossEncoder crossEncoder,
    IPaperQueryService queryService,
    ProfileService profileService,
    IOptions<RankingOptions> rankingOptions,
    IOptions<EmbeddingOptions> embeddingOptions,
    ILogger<SearchService> logger) : ISearchService
{
    private const int WildcardSlots = 2;
    private const int PoolMultiplier = 5;
    private const float CloseToHomeThreshold = 0.45f;
    private const float StretchThreshold = 0.25f;
    private const int RrfK = 60;

    public async Task<SearchResult> SearchAsync(
        SearchPlan plan, int limit, long? userId, CancellationToken ct,
        RankingWeights? weights = null)
    {
        limit = Math.Clamp(limit, 1, 200);
        var options = rankingOptions.Value;

        // An explicit weights override (the offline tuner) evaluates ONE
        // ranker: config flags still apply, interleaving never does.
        var isTuningRun = weights is not null;

        // Stage 0: deterministic SQL filters. Category/code filters still
        // materialize a candidate-id set; a date-only (or absent) filter is
        // pushed into the index scans instead — at 300k papers, SELECTing
        // every id on every search is pure waste.
        var publishedAfter = plan.DateWindowDays is { } days and > 0
            ? DateTimeOffset.UtcNow.AddDays(-days)
            : (DateTimeOffset?)null;
        var needsIdSet = plan.Categories.Count > 0 || plan.RequireNoCode == true;

        IReadOnlySet<long>? candidateIds = null;
        int candidateCount;
        if (needsIdSet)
        {
            candidateIds = (await FilterCandidates(db.Papers.AsNoTracking(), plan)
                .Select(p => p.Id)
                .ToListAsync(ct))
                .ToHashSet();
            candidateCount = candidateIds.Count;
        }
        else
        {
            candidateCount = await FilterCandidates(db.Papers.AsNoTracking(), plan).CountAsync(ct);
        }

        if (candidateCount == 0)
        {
            return new SearchResult(plan, [], 0);
        }

        var poolSize = Math.Max(limit * PoolMultiplier, limit + 50);

        List<(ScoredPaper Paper, string? Variant)> pool;
        if (!isTuningRun && options.InterleaveCandidate && options.Candidate is not null)
        {
            var control = await RankAsync(
                plan, poolSize, candidateIds, publishedAfter, options, options.ToWeights(), ct);
            var candidate = await RankAsync(
                plan, poolSize, candidateIds, publishedAfter,
                options.Candidate, options.Candidate.ToWeights(), ct);
            pool = TeamDraftInterleave(control, candidate, poolSize);
        }
        else
        {
            var ranked = await RankAsync(
                plan, poolSize, candidateIds, publishedAfter,
                options, weights ?? options.ToWeights(), ct);
            pool = ranked.Select(p => (p, (string?)null)).ToList();
        }

        if (pool.Count == 0)
        {
            logger.LogWarning(
                "Search matched {Candidates} papers but none have embeddings — run `embed` first",
                candidateCount);
            return new SearchResult(plan, [], candidateCount);
        }

        // Experience annotation + wildcards need the profile's experience vector.
        var profile = await profileService.GetAsync(userId, ct);
        IReadOnlyDictionary<long, float>? experienceScores = null;
        if (!string.IsNullOrWhiteSpace(profile?.ExperienceSummary))
        {
            var experienceVector = await embedder.EmbedAsync(profile!.ExperienceSummary, ct);
            experienceScores = await index.ScoreAsync(
                pool.Select(s => s.Paper.PaperId), experienceVector, ct);
        }
        else if (userId is not null)
        {
            // Wildcards and proximity silently vanish without an experience
            // summary; say so, or a broken exploration guarantee looks like
            // user behavior (bias report 2026-07-19: 0 wildcards ever shown).
            logger.LogInformation(
                "Search for user {UserId} has no experience summary — wildcard slots and proximity annotations are disabled",
                userId);
        }

        var variantByPaper = pool
            .Where(p => p.Variant is not null)
            .ToDictionary(p => p.Paper.PaperId, p => p.Variant);
        var selection = SelectWithWildcards(
            pool.Select(p => p.Paper).ToList(), limit, experienceScores);

        var dtos = await queryService.GetPapersByIdsAsync(
            selection.Select(s => s.Paper.PaperId).ToList(), userId, ct);

        var hits = selection
            .Where(s => dtos.ContainsKey(s.Paper.PaperId))
            .Select(s => new SearchHit(
                dtos[s.Paper.PaperId],
                s.Paper.Score,
                s.IsWildcard,
                Proximity(experienceScores, s.Paper.PaperId),
                variantByPaper.GetValueOrDefault(s.Paper.PaperId)))
            .ToList();

        return new SearchResult(plan, hits, candidateCount);
    }

    /// <summary>
    /// Runs stages 1–3 for one ranking profile. The returned Score is always
    /// the dense cosine (max-sim under multi-anchor) — the UI's match%
    /// evidence — while the ORDER carries the full pipeline's opinion.
    /// </summary>
    private async Task<List<ScoredPaper>> RankAsync(
        SearchPlan plan,
        int poolSize,
        IReadOnlySet<long>? candidateIds,
        DateTimeOffset? publishedAfter,
        RankingProfile profile,
        RankingWeights weights,
        CancellationToken ct)
    {
        // Stage 1: dense retrieval. Multi-anchor scores each paper as the
        // average of whole-intent similarity and its best single-topic match.
        var queryPrefix = embeddingOptions.Value.QueryPrefix;
        var primaryVector = await embedder.EmbedAsync(queryPrefix + plan.AnchorText, ct);

        var topicVectors = new List<float[]>();
        if (profile.UseMultiAnchor)
        {
            var topics = SplitAnchors(plan.AnchorText);
            if (topics.Count >= 2)
            {
                foreach (var topic in topics)
                {
                    topicVectors.Add(await embedder.EmbedAsync(queryPrefix + topic, ct));
                }
            }
        }

        // HyDE: the hypothetical ideal-paper abstract joins dense retrieval.
        // Embedded WITHOUT the query prefix — it's document-shaped text, and
        // the point is matching it against document-side embeddings. "anchor"
        // mode adds it to the best-topic max; "blend" folds it into the
        // whole-intent vector instead, so an off-target abstract can tilt but
        // never single-handedly hijack a paper's score.
        var hydeGatedOff = profile.UseIntentProfiles
            && string.Equals(plan.Intent, "precise", StringComparison.OrdinalIgnoreCase);
        if (profile.UseHyde && !hydeGatedOff && !string.IsNullOrWhiteSpace(plan.HypotheticalAbstract))
        {
            var hydeVector = await embedder.EmbedAsync(plan.HypotheticalAbstract, ct);
            if (string.Equals(profile.HydeMode, "blend", StringComparison.OrdinalIgnoreCase))
            {
                primaryVector = BlendUnit(primaryVector, hydeVector, profile.HydeBlendWeight);
            }
            else
            {
                topicVectors.Add(hydeVector);
            }
        }

        var pool = (await index.TopMultiAsync(
            primaryVector, topicVectors, poolSize, candidateIds, ct, publishedAfter)).ToList();
        var anchorVectors = new List<float[]> { primaryVector };
        anchorVectors.AddRange(topicVectors);

        // Stage 1b: lexical fusion.
        if (profile.UseHybrid)
        {
            var lexical = await lexicalIndex.TopAsync(
                plan.AnchorText, poolSize, candidateIds, ct, publishedAfter);
            if (pool.Count == 0 && lexical.Count > 0)
            {
                // BM25 will carry the search, so nothing visibly fails — but
                // match% reads 0 and wildcard/proximity annotations go dark.
                logger.LogWarning(
                    "Dense retrieval returned nothing (model {ModelVersion}) while BM25 matched {LexicalCount} papers — the embedding index has no vectors for this corpus/model; search is degraded to lexical-only",
                    embeddingOptions.Value.ModelVersion, lexical.Count);
            }

            pool = await FuseAsync(pool, lexical, anchorVectors, poolSize, ct);
        }

        if (pool.Count == 0)
        {
            return pool;
        }

        // Stage 2: signal blend.
        if (!weights.IsPureSimilarity)
        {
            pool = await BlendAsync(pool, weights, ct);
        }

        // Stage 3: cross-encoder rerank of the head.
        if (profile.UseReranker)
        {
            pool = await RerankHeadAsync(plan, pool, rankingOptions.Value.RerankDepth, ct);
        }

        return pool;
    }

    /// <summary>
    /// Reciprocal Rank Fusion: score = Σ 1/(60 + rank) across both lists.
    /// Papers surfaced only lexically get their dense cosine computed so the
    /// UI's match% stays meaningful for every hit.
    /// </summary>
    private async Task<List<ScoredPaper>> FuseAsync(
        IReadOnlyList<ScoredPaper> dense,
        IReadOnlyList<ScoredPaper> lexical,
        IReadOnlyList<float[]> anchorVectors,
        int poolSize,
        CancellationToken ct)
    {
        var rrf = new Dictionary<long, double>();
        for (var i = 0; i < dense.Count; i++)
        {
            rrf[dense[i].PaperId] = rrf.GetValueOrDefault(dense[i].PaperId) + 1.0 / (RrfK + i + 1);
        }

        for (var i = 0; i < lexical.Count; i++)
        {
            rrf[lexical[i].PaperId] = rrf.GetValueOrDefault(lexical[i].PaperId) + 1.0 / (RrfK + i + 1);
        }

        var cosineById = dense.ToDictionary(d => d.PaperId, d => d.Score);
        var lexOnly = lexical
            .Select(l => l.PaperId)
            .Where(id => !cosineById.ContainsKey(id))
            .ToList();

        if (lexOnly.Count > 0)
        {
            foreach (var vector in anchorVectors)
            {
                var scores = await index.ScoreAsync(lexOnly, vector, ct);
                foreach (var (id, score) in scores)
                {
                    cosineById[id] = Math.Max(cosineById.GetValueOrDefault(id, float.MinValue), score);
                }
            }
        }

        return rrf
            .OrderByDescending(kv => kv.Value)
            .Take(poolSize)
            .Select(kv => new ScoredPaper(kv.Key, cosineById.GetValueOrDefault(kv.Key)))
            .ToList();
    }

    /// <summary>
    /// Weighted re-order of the relevance pool: cosine similarity blended with
    /// recency (exponential half-life decay), a flat has-code bonus, and
    /// log-scaled citations (saturating at 1000). Fetches only pool metadata.
    /// </summary>
    private async Task<List<ScoredPaper>> BlendAsync(
        List<ScoredPaper> pool, RankingWeights weights, CancellationToken ct)
    {
        var ids = pool.Select(p => p.PaperId).ToHashSet();
        var meta = await db.Papers
            .AsNoTracking()
            .Where(p => ids.Contains(p.Id))
            .Select(p => new
            {
                p.Id,
                p.PublishedUtc,
                HasCode = p.CodeUrl != null,
                Citations = p.Signal != null ? p.Signal.CitationCount : null,
            })
            .ToDictionaryAsync(p => p.Id, ct);

        var now = DateTimeOffset.UtcNow;
        return pool
            .Select(p =>
            {
                if (!meta.TryGetValue(p.PaperId, out var m))
                {
                    return (Paper: p, Blended: (double)(weights.SimilarityWeight * p.Score));
                }

                var ageDays = Math.Max(0, (now - m.PublishedUtc).TotalDays);
                var recency = Math.Pow(2, -ageDays / weights.RecencyHalfLifeDays);
                var citations = m.Citations is { } c and > 0
                    ? Math.Min(1, Math.Log(1 + c) / Math.Log(1001))
                    : 0;
                var blended = weights.SimilarityWeight * p.Score
                    + weights.RecencyWeight * recency
                    + (m.HasCode ? weights.CodeBonus : 0)
                    + weights.CitationWeight * citations;
                return (Paper: p, Blended: blended);
            })
            .OrderByDescending(x => x.Blended)
            .ThenByDescending(x => x.Paper.Score)
            .Select(x => x.Paper)
            .ToList();
    }

    private async Task<List<ScoredPaper>> RerankHeadAsync(
        SearchPlan plan, List<ScoredPaper> pool, int depth, CancellationToken ct)
    {
        var head = pool.Take(Math.Min(depth, pool.Count)).ToList();
        var tail = pool.Skip(head.Count).ToList();

        var ids = head.Select(h => h.PaperId).ToHashSet();
        var texts = await db.Papers
            .AsNoTracking()
            .Where(p => ids.Contains(p.Id))
            .Select(p => new { p.Id, Text = p.Title + ". " + p.Abstract })
            .ToDictionaryAsync(p => p.Id, p => p.Text, ct);

        // MS MARCO cross-encoders are trained on natural-language queries;
        // the interpretation sentence matches that distribution far better
        // than the comma-separated anchor topic list (measured: topic-list
        // query DEGRADED nDCG@10 0.617 → 0.608).
        var query = string.IsNullOrWhiteSpace(plan.Interpretation)
            ? plan.AnchorText
            : plan.Interpretation;
        var passages = head.Select(h => texts.GetValueOrDefault(h.PaperId, string.Empty)).ToList();
        var scores = await crossEncoder.ScoreAsync(query, passages, ct);

        var reranked = head
            .Select((paper, i) => (Paper: paper, CeScore: scores[i]))
            .OrderByDescending(x => x.CeScore)
            .Select(x => x.Paper)
            .ToList();

        reranked.AddRange(tail);
        return reranked;
    }

    /// <summary>
    /// Team-draft interleaving: teams alternate (random first pick per round)
    /// drafting their best not-yet-taken paper. Interactions on A-slots vs
    /// B-slots are votes between the rankers.
    /// </summary>
    internal static List<(ScoredPaper Paper, string? Variant)> TeamDraftInterleave(
        IReadOnlyList<ScoredPaper> control,
        IReadOnlyList<ScoredPaper> candidate,
        int limit,
        Func<bool>? coinFlip = null)
    {
        coinFlip ??= () => Random.Shared.Next(2) == 0;
        var taken = new HashSet<long>();
        var result = new List<(ScoredPaper, string?)>(limit);
        var ai = 0;
        var bi = 0;

        while (result.Count < limit && (ai < control.Count || bi < candidate.Count))
        {
            var aFirst = coinFlip();
            foreach (var team in aFirst ? new[] { "A", "B" } : ["B", "A"])
            {
                if (result.Count >= limit)
                {
                    break;
                }

                if (team == "A")
                {
                    while (ai < control.Count && taken.Contains(control[ai].PaperId))
                    {
                        ai++;
                    }

                    if (ai < control.Count)
                    {
                        taken.Add(control[ai].PaperId);
                        result.Add((control[ai], "A"));
                        ai++;
                    }
                }
                else
                {
                    while (bi < candidate.Count && taken.Contains(candidate[bi].PaperId))
                    {
                        bi++;
                    }

                    if (bi < candidate.Count)
                    {
                        taken.Add(candidate[bi].PaperId);
                        result.Add((candidate[bi], "B"));
                        bi++;
                    }
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Anchor text is a comma-separated topic list by construction (the
    /// compiler's contract); each topic becomes its own query vector. Falls
    /// back to the whole text when there's nothing to split.
    /// </summary>
    /// <summary>Weighted sum of two unit vectors, renormalized — cosine against
    /// the result is the (weighted) average of the cosines against each part.
    /// bWeight is b's share; a gets the rest.</summary>
    internal static float[] BlendUnit(float[] a, float[] b, float bWeight = 0.5f)
    {
        var v = new float[a.Length];
        var normSq = 0f;
        for (var i = 0; i < a.Length; i++)
        {
            v[i] = (1 - bWeight) * a[i] + bWeight * b[i];
            normSq += v[i] * v[i];
        }

        var norm = MathF.Sqrt(normSq);
        if (norm > 0)
        {
            for (var i = 0; i < v.Length; i++)
            {
                v[i] /= norm;
            }
        }

        return v;
    }

    internal static List<string> SplitAnchors(string anchorText)
    {
        var topics = anchorText
            .Split([',', ';', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(t => t.Length >= 3)
            .Take(20)
            .ToList();

        return topics.Count >= 2 ? topics : [anchorText];
    }

    /// <summary>
    /// Stage-0 filters as a reusable query transform: the eval harness applies
    /// the exact same candidate definition when sampling papers to judge, so
    /// offline metrics measure the ranker, not a diverged filter.
    /// </summary>
    internal static IQueryable<Domain.Entities.Paper> FilterCandidates(
        IQueryable<Domain.Entities.Paper> papers, SearchPlan plan)
    {
        if (plan.Categories.Count > 0)
        {
            var codes = plan.Categories;
            papers = papers.Where(
                p => p.PaperCategories.Any(pc => codes.Contains(pc.Category.Code)));
        }

        if (plan.DateWindowDays is { } days and > 0)
        {
            var cutoff = DateTimeOffset.UtcNow.AddDays(-days);
            papers = papers.Where(p => p.PublishedUtc >= cutoff);
        }

        if (plan.RequireNoCode == true)
        {
            papers = papers.Where(p => p.CodeUrl == null);
        }

        return papers;
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
