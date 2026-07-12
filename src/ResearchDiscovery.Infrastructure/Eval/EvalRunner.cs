using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ResearchDiscovery.Application.Abstractions;
using ResearchDiscovery.Application.Eval;
using ResearchDiscovery.Application.Options;
using ResearchDiscovery.Infrastructure.Persistence;
using ResearchDiscovery.Infrastructure.Search;

namespace ResearchDiscovery.Infrastructure.Eval;

/// <summary>
/// Orchestrates the offline search-quality harness around two versioned
/// artifacts (queries.json, judgments.json):
///
///   compile — fills missing SearchPlans via the LLM compiler (tokens; rare)
///   judge   — grades pool ∪ random-sample papers per query (tokens; additive)
///   search  — runs frozen plans through the live ranker and scores against
///             judgments (zero tokens; run on every ranking change)
///
/// Judgments are append-only across ranker versions: as rankers change, their
/// pooled heads accumulate into the artifact and recall estimates get more
/// honest (standard TREC-style pooling).
/// </summary>
public class EvalRunner(
    IDbContextFactory<AppDbContext> dbFactory,
    ISearchService search,
    ISearchPlanCompiler compiler,
    IRelevanceJudge judge,
    ILogger<EvalRunner> logger)
{
    private const int ScoreLimit = 50;

    public async Task<int> CompileAsync(string queriesPath, CancellationToken ct)
    {
        var set = EvalFileStore.LoadQueries(queriesPath);

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var knownCategories = await db.Categories
            .AsNoTracking()
            .OrderBy(c => c.Code)
            .Select(c => c.Code)
            .ToListAsync(ct);

        var compiled = 0;
        var queries = set.Queries.ToList();
        for (var i = 0; i < queries.Count; i++)
        {
            if (queries[i].Plan is { HypotheticalAbstract: not null, Intent: not null })
            {
                continue;
            }

            var q = queries[i];
            if (q.Plan is null)
            {
                var plan = await compiler.CompileAsync(q.Query, q.Persona, knownCategories, ct);
                queries[i] = q with { Plan = plan };
                logger.LogInformation(
                    "Compiled eval query {Id}: {Interpretation}", q.Id, plan.Interpretation);
            }
            else
            {
                // Plan predates newer fields: recompile but graft ONLY the
                // missing fields onto the frozen plan. Anchors/categories must
                // stay bit-identical or every baseline comparison is contaminated.
                var fresh = await compiler.CompileAsync(q.Query, q.Persona, knownCategories, ct);
                queries[i] = q with
                {
                    Plan = q.Plan with
                    {
                        HypotheticalAbstract = q.Plan.HypotheticalAbstract ?? fresh.HypotheticalAbstract,
                        Intent = q.Plan.Intent ?? fresh.Intent,
                    },
                };
                logger.LogInformation("Backfilled missing plan fields for eval query {Id}", q.Id);
            }
            compiled++;

            // Save after each compile so an interrupted run keeps its progress.
            EvalFileStore.SaveQueries(queriesPath, set with { Queries = queries });
        }

        return compiled;
    }

    public async Task<int> JudgeAsync(
        string queriesPath, string judgmentsPath, int poolSize, int randomSample, CancellationToken ct)
    {
        var querySet = EvalFileStore.LoadQueries(queriesPath);
        var judgmentSet = EvalFileStore.LoadJudgmentsOrEmpty(judgmentsPath, judge.RubricVersion);

        if (judgmentSet.RubricVersion != judge.RubricVersion)
        {
            throw new InvalidOperationException(
                $"Judgments were graded with rubric v{judgmentSet.RubricVersion} but the judge is " +
                $"v{judge.RubricVersion}. Regrade from scratch (delete the file) or keep the old rubric.");
        }

        var already = judgmentSet.Judgments
            .Select(j => (j.QueryId, j.ArxivId))
            .ToHashSet();

        var totalNew = 0;
        foreach (var query in querySet.Queries)
        {
            if (query.Plan is null)
            {
                logger.LogWarning("Eval query {Id} has no compiled plan — run `eval compile` first", query.Id);
                continue;
            }

            var pool = await BuildPoolAsync(query, poolSize, randomSample, ct);
            var unjudged = pool
                .Where(p => !already.Contains((query.Id, p.Candidate.ArxivId)))
                .ToList();

            if (unjudged.Count == 0)
            {
                logger.LogInformation("Eval query {Id}: all {Count} pooled papers already judged", query.Id, pool.Count);
                continue;
            }

            var result = await judge.JudgeAsync(query, unjudged.Select(p => p.Candidate).ToList(), ct);
            var sourceByArxivId = unjudged.ToDictionary(p => p.Candidate.ArxivId, p => p.Source, StringComparer.Ordinal);

            var fresh = result.Verdicts
                .Select(v => new EvalJudgment(
                    query.Id, v.ArxivId, v.Grade, v.Rationale, result.ModelId, sourceByArxivId[v.ArxivId]))
                .ToList();

            judgmentSet = judgmentSet with { Judgments = [.. judgmentSet.Judgments, .. fresh] };
            foreach (var j in fresh)
            {
                already.Add((j.QueryId, j.ArxivId));
            }

            totalNew += fresh.Count;

            // Save per query so an interrupted judging run keeps its progress.
            EvalFileStore.SaveJudgments(judgmentsPath, judgmentSet);
            logger.LogInformation(
                "Eval query {Id}: judged {New} new papers ({Skipped} already judged)",
                query.Id, fresh.Count, pool.Count - unjudged.Count);
        }

        return totalNew;
    }

    public async Task<EvalReport> ScoreAsync(
        string queriesPath, string judgmentsPath, CancellationToken ct, RankingWeights? weights = null)
    {
        var querySet = EvalFileStore.LoadQueries(queriesPath);
        var judgmentSet = EvalFileStore.LoadJudgmentsOrEmpty(judgmentsPath, judge.RubricVersion);

        var byQuery = judgmentSet.Judgments
            .GroupBy(j => j.QueryId, StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyDictionary<string, int>)g
                    .GroupBy(j => j.ArxivId, StringComparer.Ordinal)
                    .ToDictionary(x => x.Key, x => x.First().Grade, StringComparer.Ordinal),
                StringComparer.Ordinal);

        var scores = new List<EvalQueryScore>();
        foreach (var query in querySet.Queries)
        {
            if (query.Plan is null || !byQuery.TryGetValue(query.Id, out var grades))
            {
                continue;
            }

            // userId null: eval measures the unpersonalized ranker (experience
            // annotation never ranks anyway, and eval must not depend on
            // whoever happens to run it).
            var result = await search.SearchAsync(query.Plan, ScoreLimit, null, ct, weights);

            // Wildcards are contractual serendipity, not ranking claims — the
            // metric measures the relevance ordering only.
            var ranked = result.Hits
                .Where(h => !h.IsWildcard)
                .Select(h => h.Paper.ArxivId)
                .ToList();

            scores.Add(new EvalQueryScore(
                query.Id,
                RankingMetrics.NdcgAtK(ranked, grades, 10),
                RankingMetrics.RecallAtK(ranked, grades, ScoreLimit),
                RankingMetrics.ReciprocalRank(ranked, grades),
                JudgedInTop10: ranked.Take(10).Count(grades.ContainsKey),
                JudgedTotal: grades.Count,
                RelevantTotal: grades.Values.Count(g => g >= RankingMetrics.RelevantThreshold)));
        }

        return new EvalReport(
            scores,
            Mean(scores.Select(s => s.Ndcg10)),
            Mean(scores.Select(s => s.Recall50)),
            Mean(scores.Select(s => s.ReciprocalRank)));
    }

    public sealed record AuditQueryEstimate(
        string QueryId,
        int RandomJudged,
        int RandomRelevant,
        int CandidateCount,
        int SurfacedRelevant,
        double? EstimatedMissedGems);

    /// <summary>
    /// Missed-gem estimation from the judged RANDOM samples: if r of n
    /// uniformly-sampled unreturned candidates are relevant, the filtered pool
    /// hides ≈ (r/n) × (pool − returned) more gems the ranker never surfaced.
    /// Small n means wide error bars — this is a trend indicator, not a count.
    /// </summary>
    public async Task<IReadOnlyList<AuditQueryEstimate>> AuditAsync(
        string queriesPath, string judgmentsPath, CancellationToken ct)
    {
        var querySet = EvalFileStore.LoadQueries(queriesPath);
        var judgmentSet = EvalFileStore.LoadJudgmentsOrEmpty(judgmentsPath, judge.RubricVersion);
        var byQuery = judgmentSet.Judgments
            .GroupBy(j => j.QueryId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var estimates = new List<AuditQueryEstimate>();
        foreach (var query in querySet.Queries)
        {
            if (query.Plan is null || !byQuery.TryGetValue(query.Id, out var judgments))
            {
                continue;
            }

            var random = judgments.Where(j => j.Source == "random").ToList();
            var randomRelevant = random.Count(j => j.Grade >= RankingMetrics.RelevantThreshold);
            var surfacedRelevant = judgments.Count(
                j => j.Source == "pool" && j.Grade >= RankingMetrics.RelevantThreshold);

            var candidateCount = await SearchService
                .FilterCandidates(db.Papers.AsNoTracking(), query.Plan)
                .CountAsync(ct);

            double? estimate = random.Count > 0
                ? (double)randomRelevant / random.Count * Math.Max(0, candidateCount - 50)
                : null;

            estimates.Add(new AuditQueryEstimate(
                query.Id, random.Count, randomRelevant, candidateCount, surfacedRelevant, estimate));
        }

        return estimates;
    }

    public sealed record TuneResult(
        RankingWeights Weights, double? MeanNdcg10, double? MeanRecall50, double? MeanMrr);

    /// <summary>
    /// Offline grid search over the ranking-blend weights, scoring each combo
    /// against the judged ground truth. Returns results sorted best-first;
    /// deliberately does NOT apply anything — a human reads the table, decides,
    /// and sets the Ranking config. The (0, 0) combo is the current default
    /// ranker, so the baseline is always in the table.
    /// </summary>
    public async Task<IReadOnlyList<TuneResult>> TuneAsync(
        string queriesPath, string judgmentsPath, CancellationToken ct)
    {
        float[] recencyWeights = [0f, 0.05f, 0.1f, 0.2f];
        float[] codeBonuses = [0f, 0.02f, 0.05f];
        float[] citationWeights = [0f, 0.05f, 0.1f];
        const int halfLifeDays = 90;

        var results = new List<TuneResult>();
        foreach (var recency in recencyWeights)
        {
            foreach (var code in codeBonuses)
            {
                foreach (var citation in citationWeights)
                {
                    var weights = new RankingWeights(1f, recency, halfLifeDays, code, citation);
                    var report = await ScoreAsync(queriesPath, judgmentsPath, ct, weights);
                    results.Add(new TuneResult(
                        weights, report.MeanNdcg10, report.MeanRecall50, report.MeanReciprocalRank));
                    logger.LogInformation(
                        "Tune: recency={Recency} code={Code} citation={Citation} → nDCG@10 {Ndcg:F3}",
                        recency, code, citation, report.MeanNdcg10 ?? 0);
                }
            }
        }

        return results
            .OrderByDescending(r => r.MeanNdcg10 ?? 0)
            .ToList();
    }

    /// <summary>
    /// The judgment pool for a query: the ranker's head (what pooled metrics
    /// need) plus a deterministic uniform sample of the remaining filtered
    /// candidates (what recall honesty and missed-gem estimation need).
    /// </summary>
    private async Task<List<(JudgeCandidate Candidate, string Source)>> BuildPoolAsync(
        EvalQuery query, int poolSize, int randomSample, CancellationToken ct)
    {
        var result = await search.SearchAsync(query.Plan!, poolSize, null, ct);
        var pooled = result.Hits.Select(h => h.Paper.ArxivId).ToList();
        var pooledSet = pooled.ToHashSet(StringComparer.Ordinal);

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var sampled = new List<string>();
        if (randomSample > 0)
        {
            var candidateIds = await SearchService
                .FilterCandidates(db.Papers.AsNoTracking(), query.Plan!)
                .Select(p => p.ArxivId)
                .ToListAsync(ct);

            // Seeded by the query id (stable across processes, unlike
            // string.GetHashCode) so re-runs sample the same papers and the
            // incremental judge skips them instead of paying to grade new ones.
            var random = new Random(StableHash(query.Id));
            sampled = candidateIds
                .Where(id => !pooledSet.Contains(id))
                .OrderBy(_ => random.Next())
                .Take(randomSample)
                .ToList();
        }

        var wanted = pooled.Concat(sampled).ToHashSet(StringComparer.Ordinal);
        var texts = await db.Papers
            .AsNoTracking()
            .Where(p => wanted.Contains(p.ArxivId))
            .Select(p => new { p.ArxivId, p.Title, p.Abstract })
            .ToDictionaryAsync(p => p.ArxivId, StringComparer.Ordinal, ct);

        var pool = new List<(JudgeCandidate, string)>();
        foreach (var id in pooled)
        {
            if (texts.TryGetValue(id, out var t))
            {
                pool.Add((new JudgeCandidate(id, t.Title, t.Abstract), "pool"));
            }
        }

        foreach (var id in sampled)
        {
            if (texts.TryGetValue(id, out var t))
            {
                pool.Add((new JudgeCandidate(id, t.Title, t.Abstract), "random"));
            }
        }

        return pool;
    }

    private static double? Mean(IEnumerable<double?> values)
    {
        var defined = values.Where(v => v.HasValue).Select(v => v!.Value).ToList();
        return defined.Count == 0 ? null : defined.Average();
    }

    /// <summary>FNV-1a — string.GetHashCode is randomized per process, this is not.</summary>
    internal static int StableHash(string text)
    {
        unchecked
        {
            var hash = 2166136261u;
            foreach (var c in text)
            {
                hash = (hash ^ c) * 16777619u;
            }

            return (int)hash;
        }
    }
}
