using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ResearchDiscovery.Application.Abstractions;
using ResearchDiscovery.Application.Eval;
using ResearchDiscovery.Application.Options;
using ResearchDiscovery.Domain.Entities;
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

    /// <summary>
    /// The Tier 2 category-inference eval: compile a FRESH plan for every
    /// query carrying ExpectedCategories and score the emitted categories
    /// against the authored expectations. Fresh compiles are deliberate —
    /// this measures the CURRENT compiler prompt (frozen plans stay the
    /// untouched nDCG baseline) — and are never persisted into the artifact.
    /// Costs one compiler call per target query per run (pennies at haiku
    /// tier). Compiler sampling makes single runs noisy (±0.1 per-query
    /// precision measured 2026-07-19), so prompt comparisons should use
    /// runs >= 3; metrics are averaged per query across runs.
    /// </summary>
    public async Task<CategoryInferenceReport> ScoreCategoriesAsync(
        string queriesPath, int runs, CancellationToken ct)
    {
        var set = EvalFileStore.LoadQueries(queriesPath);
        var targets = set.Queries.Where(q => q.ExpectedCategories is not null).ToList();
        if (targets.Count == 0)
        {
            return new CategoryInferenceReport(runs, [], [], null, null, null, null);
        }

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var knownCategories = await db.Categories
            .AsNoTracking()
            .OrderBy(c => c.Code)
            .Select(c => c.Code)
            .ToListAsync(ct);

        var perQuery = targets.ToDictionary(
            q => q.Id, _ => new List<CategoryInferenceScore>(), StringComparer.Ordinal);
        var runMeanF1 = new List<double>();
        for (var run = 1; run <= runs; run++)
        {
            var runScores = new List<CategoryInferenceScore>();
            foreach (var q in targets)
            {
                ct.ThrowIfCancellationRequested();
                var plan = await compiler.CompileAsync(q.Query, q.Persona, knownCategories, ct);
                var score = CategoryInferenceMetrics.Score(
                    q.Id, plan.Categories, q.ExpectedCategories!, q.AcceptableCategories, knownCategories);
                perQuery[q.Id].Add(score);
                runScores.Add(score);
                logger.LogInformation(
                    "Category inference {Id} (run {Run}/{Runs}): emitted [{Emitted}] P={Precision:0.00} R={Recall:0.00}",
                    q.Id, run, runs, string.Join(", ", score.Emitted), score.Precision, score.Recall);
            }

            runMeanF1.Add(runScores.Average(s => s.F1));
        }

        var aggregates = targets
            .Select(q => CategoryInferenceMetrics.Aggregate(perQuery[q.Id]))
            .ToList();
        return new CategoryInferenceReport(
            runs,
            aggregates,
            runMeanF1,
            aggregates.Average(a => a.MeanPrecision),
            aggregates.Average(a => a.MeanRecall),
            aggregates.Average(a => a.MeanReachableRecall),
            aggregates.Average(a => a.MeanF1));
    }

    public sealed record CorpusFixturePaper(
        string ArxivId,
        int LatestVersion,
        string Title,
        string Abstract,
        string Authors,
        string PrimaryCategory,
        IReadOnlyList<string> Categories,
        DateTimeOffset PublishedUtc,
        DateTimeOffset UpdatedUtc,
        string AbsUrl,
        string PdfUrl,
        string? Doi,
        string? CodeUrl);

    public sealed record CorpusFixture(int Version, IReadOnlyList<CorpusFixturePaper> Papers);

    /// <summary>
    /// Exports every judged paper as a corpus fixture — the checked-in
    /// mini-corpus the CI regression gate scores against. Embeddings are NOT
    /// exported: they're deterministic per model version, so CI re-embeds
    /// (`embed` command) and the fixture stays small and diffable.
    /// </summary>
    public async Task<int> ExportCorpusAsync(string judgmentsPath, string outPath, CancellationToken ct)
    {
        var judgmentSet = EvalFileStore.LoadJudgmentsOrEmpty(judgmentsPath, judge.RubricVersion);
        var ids = judgmentSet.Judgments.Select(j => j.ArxivId).Distinct().ToList();

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var papers = await db.Papers
            .AsNoTracking()
            .Include(p => p.PrimaryCategory)
            .Include(p => p.PaperCategories).ThenInclude(pc => pc.Category)
            .Where(p => ids.Contains(p.ArxivId))
            .ToListAsync(ct);

        var fixture = new CorpusFixture(
            1,
            papers
                .Select(p => new CorpusFixturePaper(
                    p.ArxivId,
                    p.LatestVersion,
                    p.Title,
                    p.Abstract,
                    p.Authors,
                    p.PrimaryCategory.Code,
                    p.PaperCategories.Select(pc => pc.Category.Code).OrderBy(c => c, StringComparer.Ordinal).ToList(),
                    p.PublishedUtc,
                    p.UpdatedUtc,
                    p.AbsUrl,
                    p.PdfUrl,
                    p.Doi,
                    p.CodeUrl))
                .OrderBy(p => p.ArxivId, StringComparer.Ordinal)
                .ToList());

        EvalFileStore.SaveCorpus(outPath, fixture);
        logger.LogInformation(
            "Exported {Count} judged papers ({Missing} judged ids not in this corpus) to {Path}",
            fixture.Papers.Count, ids.Count - fixture.Papers.Count, outPath);
        return fixture.Papers.Count;
    }

    /// <summary>
    /// Loads a corpus fixture into an EMPTY database (refuses otherwise — this
    /// is a CI/scratch operation, never something to aim at a real corpus).
    /// </summary>
    public async Task<int> SeedCorpusAsync(string corpusPath, CancellationToken ct)
    {
        var fixture = EvalFileStore.LoadCorpus(corpusPath);

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        if (await db.Papers.AnyAsync(ct))
        {
            throw new InvalidOperationException(
                "Refusing to seed: the corpus is not empty. `eval seed` targets fresh CI/scratch databases only.");
        }

        var categories = new Dictionary<string, Category>(StringComparer.OrdinalIgnoreCase);
        Category GetCategory(string code)
        {
            if (!categories.TryGetValue(code, out var category))
            {
                category = new Category { Code = code, Name = code };
                categories[code] = category;
                db.Categories.Add(category);
            }

            return category;
        }

        foreach (var p in fixture.Papers)
        {
            var paper = new Paper
            {
                ArxivId = p.ArxivId,
                LatestVersion = p.LatestVersion,
                Title = p.Title,
                Abstract = p.Abstract,
                Authors = p.Authors,
                PrimaryCategory = GetCategory(p.PrimaryCategory),
                PublishedUtc = p.PublishedUtc,
                UpdatedUtc = p.UpdatedUtc,
                AbsUrl = p.AbsUrl,
                PdfUrl = p.PdfUrl,
                Doi = p.Doi,
                CodeUrl = p.CodeUrl,
                // Deterministic bookkeeping: the fixture must produce the same
                // DB bytes on every CI run.
                FirstIngestedUtc = p.UpdatedUtc,
                LastSeenUtc = p.UpdatedUtc,
            };
            foreach (var code in p.Categories)
            {
                paper.PaperCategories.Add(new PaperCategory { Paper = paper, Category = GetCategory(code) });
            }

            db.Papers.Add(paper);
        }

        await db.SaveChangesAsync(ct);
        logger.LogInformation("Seeded {Count} papers from {Path}", fixture.Papers.Count, corpusPath);
        return fixture.Papers.Count;
    }

    public sealed record CalibrationPair(
        string QueryId,
        string ArxivId,
        int OriginalGrade,
        int CalibrationGrade,
        string OriginalRationale,
        string CalibrationRationale);

    public sealed record CalibrationReport(
        string CalibrationModel,
        string OriginalModels,
        int RubricVersion,
        int SampleSize,
        double ExactAgreement,
        double WithinOneAgreement,
        double QuadraticWeightedKappa,
        int[][] Confusion, // [originalGrade][calibrationGrade]
        IReadOnlyList<CalibrationPair> Pairs);

    /// <summary>
    /// Double-judges a stratified, seeded sample of EXISTING judgments with a
    /// (stronger) override model and reports agreement — the check on whether
    /// the ground truth itself can be trusted. Writes a separate calibration
    /// artifact; never touches judgments.json.
    /// </summary>
    public async Task<CalibrationReport> CalibrateAsync(
        string queriesPath,
        string judgmentsPath,
        string calibrationPath,
        int sampleSize,
        string modelId,
        CancellationToken ct)
    {
        var querySet = EvalFileStore.LoadQueries(queriesPath);
        var judgmentSet = EvalFileStore.LoadJudgmentsOrEmpty(judgmentsPath, judge.RubricVersion);
        if (judgmentSet.RubricVersion != judge.RubricVersion)
        {
            throw new InvalidOperationException(
                $"Judgments are rubric v{judgmentSet.RubricVersion}, judge is v{judge.RubricVersion} — " +
                "calibration across rubrics is meaningless.");
        }

        var queriesById = querySet.Queries
            .Where(q => q.Plan is not null)
            .ToDictionary(q => q.Id, StringComparer.Ordinal);

        // Stratified sample: equal share per grade, deterministic via a fixed
        // seed over a stable ordering — reruns sample the same pairs.
        var rng = new Random(20260712);
        var perGrade = (int)Math.Ceiling(sampleSize / 4.0);
        var sampled = judgmentSet.Judgments
            .Where(j => queriesById.ContainsKey(j.QueryId))
            .GroupBy(j => j.Grade)
            .OrderBy(g => g.Key)
            .SelectMany(g => g
                .OrderBy(j => j.QueryId, StringComparer.Ordinal)
                .ThenBy(j => j.ArxivId, StringComparer.Ordinal)
                .OrderBy(_ => rng.Next())
                .Take(perGrade))
            .ToList();

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var arxivIds = sampled.Select(s => s.ArxivId).Distinct().ToList();
        var papers = await db.Papers
            .AsNoTracking()
            .Where(p => arxivIds.Contains(p.ArxivId))
            .Select(p => new { p.ArxivId, p.Title, p.Abstract })
            .ToDictionaryAsync(p => p.ArxivId, StringComparer.Ordinal, ct);

        var pairs = new List<CalibrationPair>();
        foreach (var group in sampled.GroupBy(s => s.QueryId))
        {
            ct.ThrowIfCancellationRequested();
            var query = queriesById[group.Key];
            var candidates = group
                .Where(s => papers.ContainsKey(s.ArxivId))
                .Select(s => new JudgeCandidate(
                    s.ArxivId, papers[s.ArxivId].Title, papers[s.ArxivId].Abstract))
                .ToList();
            if (candidates.Count == 0)
            {
                continue; // papers pruned from the corpus since judging
            }

            var result = await judge.JudgeAsync(query, candidates, ct, modelId);
            var verdictById = result.Verdicts.ToDictionary(v => v.ArxivId, StringComparer.Ordinal);
            foreach (var original in group)
            {
                if (verdictById.TryGetValue(original.ArxivId, out var v))
                {
                    pairs.Add(new CalibrationPair(
                        original.QueryId, original.ArxivId,
                        original.Grade, v.Grade, original.Rationale, v.Rationale));
                }
            }

            logger.LogInformation(
                "Calibration: {QueryId} re-judged {Count} pair(s)", query.Id, candidates.Count);
        }

        var confusion = new int[4][];
        for (var i = 0; i < 4; i++)
        {
            confusion[i] = new int[4];
        }

        foreach (var p in pairs)
        {
            confusion[p.OriginalGrade][p.CalibrationGrade]++;
        }

        var originalModels = string.Join(
            ", ",
            sampled.Select(s => s.JudgeModel).Distinct(StringComparer.Ordinal).OrderBy(m => m));

        var report = new CalibrationReport(
            modelId,
            originalModels,
            judge.RubricVersion,
            pairs.Count,
            pairs.Count == 0 ? 0 : pairs.Count(p => p.OriginalGrade == p.CalibrationGrade) / (double)pairs.Count,
            pairs.Count == 0 ? 0 : pairs.Count(p => Math.Abs(p.OriginalGrade - p.CalibrationGrade) <= 1) / (double)pairs.Count,
            QuadraticWeightedKappa(confusion, pairs.Count),
            confusion,
            pairs);

        EvalFileStore.SaveCalibration(calibrationPath, report);
        return report;
    }

    /// <summary>Cohen's kappa with quadratic (squared-distance) weights — the
    /// standard agreement statistic for ordinal grades; 1 = perfect, 0 = chance.</summary>
    internal static double QuadraticWeightedKappa(int[][] confusion, int total)
    {
        if (total == 0)
        {
            return 0;
        }

        var rowSums = confusion.Select(r => r.Sum()).ToArray();
        var colSums = Enumerable.Range(0, 4).Select(c => confusion.Sum(r => r[c])).ToArray();

        double observed = 0, expected = 0;
        for (var i = 0; i < 4; i++)
        {
            for (var j = 0; j < 4; j++)
            {
                var weight = (i - j) * (i - j) / 9.0; // max distance² = 3² = 9
                observed += weight * confusion[i][j];
                expected += weight * rowSums[i] * colSums[j] / (double)total;
            }
        }

        return expected == 0 ? 1 : 1 - observed / expected;
    }

    /// <summary>
    /// Re-grades EVERY existing judgment under the judge's current rubric,
    /// preserving each pair's Source (pooling history survives a rubric bump).
    /// Writes progressively to a temp file and swaps atomically at the end, so
    /// an interrupted run never leaves a mixed-rubric artifact.
    /// </summary>
    public async Task<int> RegradeAsync(string queriesPath, string judgmentsPath, CancellationToken ct)
    {
        var querySet = EvalFileStore.LoadQueries(queriesPath);
        var old = EvalFileStore.LoadJudgmentsOrEmpty(judgmentsPath, judge.RubricVersion);
        var queriesById = querySet.Queries
            .Where(q => q.Plan is not null)
            .ToDictionary(q => q.Id, StringComparer.Ordinal);

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var arxivIds = old.Judgments.Select(j => j.ArxivId).Distinct().ToList();
        var papers = await db.Papers
            .AsNoTracking()
            .Where(p => arxivIds.Contains(p.ArxivId))
            .Select(p => new { p.ArxivId, p.Title, p.Abstract })
            .ToDictionaryAsync(p => p.ArxivId, StringComparer.Ordinal, ct);

        var tempPath = judgmentsPath + ".regrade.tmp";
        var regraded = new List<EvalJudgment>();
        foreach (var group in old.Judgments.GroupBy(j => j.QueryId))
        {
            ct.ThrowIfCancellationRequested();
            if (!queriesById.TryGetValue(group.Key, out var query))
            {
                logger.LogWarning(
                    "Regrade: dropping {Count} judgment(s) for removed query {QueryId}",
                    group.Count(), group.Key);
                continue;
            }

            var sourceById = group.ToDictionary(j => j.ArxivId, j => j.Source, StringComparer.Ordinal);
            var candidates = group
                .Where(j => papers.ContainsKey(j.ArxivId))
                .Select(j => new JudgeCandidate(j.ArxivId, papers[j.ArxivId].Title, papers[j.ArxivId].Abstract))
                .ToList();
            if (candidates.Count < group.Count())
            {
                logger.LogWarning(
                    "Regrade: {Missing} paper(s) for {QueryId} no longer in the corpus — dropped",
                    group.Count() - candidates.Count, group.Key);
            }

            if (candidates.Count == 0)
            {
                continue;
            }

            var result = await judge.JudgeAsync(query, candidates, ct);
            regraded.AddRange(result.Verdicts.Select(v => new EvalJudgment(
                group.Key, v.ArxivId, v.Grade, v.Rationale, result.ModelId, sourceById[v.ArxivId])));

            EvalFileStore.SaveJudgments(tempPath, new EvalJudgmentSet(1, judge.RubricVersion, regraded));
            logger.LogInformation(
                "Regrade: {QueryId} done ({Total} pairs so far)", group.Key, regraded.Count);
        }

        File.Move(tempPath, judgmentsPath, overwrite: true);
        return regraded.Count;
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
