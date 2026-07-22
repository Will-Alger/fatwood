namespace ResearchDiscovery.Application.Eval;

/// <summary>
/// Score of one query's compiler-emitted categories against authored
/// expectations. UnreachableExpected are expected codes absent from the
/// corpus taxonomy — the compiler cannot pick them, so they are a corpus
/// gap (Tier 2 Phase C signal), not a compiler failure; ReachableRecall
/// excludes them so pre-Phase-C numbers stay compiler-attributable.
/// </summary>
public sealed record CategoryInferenceScore(
    string QueryId,
    IReadOnlyList<string> Emitted,
    IReadOnlyList<string> ExpectedHit,
    IReadOnlyList<string> ExpectedMissed,
    IReadOnlyList<string> Unexpected,
    IReadOnlyList<string> UnreachableExpected,
    double Precision,
    double Recall,
    double ReachableRecall,
    double F1);

/// <summary>A category with the number of runs in which it appeared.</summary>
public sealed record CategoryRunCount(string Category, int Runs);

/// <summary>
/// One query's scores aggregated over N fresh compiles. Metrics are means
/// across runs; the category lists carry per-run occurrence counts so an
/// unstable emission (picked in 1 of 3 runs) is visible, not averaged away.
/// MinF1/MaxF1 bound the single-run spread the compiler's sampling produces.
/// </summary>
public sealed record CategoryInferenceQueryAggregate(
    string QueryId,
    int Runs,
    IReadOnlyList<CategoryRunCount> Emitted,
    IReadOnlyList<CategoryRunCount> ExpectedMissed,
    IReadOnlyList<CategoryRunCount> Unexpected,
    IReadOnlyList<string> UnreachableExpected,
    double MeanPrecision,
    double MeanRecall,
    double MeanReachableRecall,
    double MeanF1,
    double MinF1,
    double MaxF1);

public sealed record CategoryInferenceReport(
    int Runs,
    IReadOnlyList<CategoryInferenceQueryAggregate> Queries,
    IReadOnlyList<double> RunMeanF1,
    double? MeanPrecision,
    double? MeanRecall,
    double? MeanReachableRecall,
    double? MeanF1);

public static class CategoryInferenceMetrics
{
    /// <summary>
    /// Set precision/recall over category codes (ordinal, case-insensitive).
    /// Conventions: empty expected = "no filter is correct" (recall 1.0, any
    /// emitted code not in acceptable is a precision error); empty emitted =
    /// precision 1.0 (picking nothing picks nothing wrong — recall alone
    /// punishes an empty pick when a slice was expected). F1 pairs precision
    /// with ReachableRecall.
    /// </summary>
    public static CategoryInferenceScore Score(
        string queryId,
        IReadOnlyList<string> emitted,
        IReadOnlyList<string> expected,
        IReadOnlyList<string>? acceptable,
        IReadOnlyCollection<string> knownCategories)
    {
        var cmp = StringComparer.OrdinalIgnoreCase;
        var emittedSet = emitted.Distinct(cmp).ToList();
        var expectedSet = expected.Distinct(cmp).ToList();
        var known = knownCategories.ToHashSet(cmp);
        var allowed = expectedSet.Concat(acceptable ?? []).ToHashSet(cmp);

        var unreachable = expectedSet.Where(c => !known.Contains(c)).ToList();
        var reachable = expectedSet.Where(known.Contains).ToList();

        var hit = expectedSet.Where(c => emittedSet.Contains(c, cmp)).ToList();
        var missed = expectedSet.Where(c => !emittedSet.Contains(c, cmp)).ToList();
        var unexpected = emittedSet.Where(c => !allowed.Contains(c)).ToList();

        var precision = emittedSet.Count == 0
            ? 1.0
            : (double)(emittedSet.Count - unexpected.Count) / emittedSet.Count;
        var recall = expectedSet.Count == 0
            ? 1.0
            : (double)hit.Count / expectedSet.Count;
        var reachableHit = reachable.Count(c => emittedSet.Contains(c, cmp));
        var reachableRecall = reachable.Count == 0
            ? 1.0
            : (double)reachableHit / reachable.Count;
        var f1 = precision + reachableRecall == 0
            ? 0.0
            : 2 * precision * reachableRecall / (precision + reachableRecall);

        return new CategoryInferenceScore(
            queryId, emittedSet, hit, missed, unexpected, unreachable,
            precision, recall, reachableRecall, f1);
    }

    /// <summary>
    /// Aggregates one query's scores across N runs (compiler sampling makes
    /// single runs noisy — see the Tier 2 B.2 parsimony note). All runs must
    /// score the same query.
    /// </summary>
    public static CategoryInferenceQueryAggregate Aggregate(
        IReadOnlyList<CategoryInferenceScore> runs)
    {
        if (runs.Count == 0)
        {
            throw new ArgumentException("At least one run is required.", nameof(runs));
        }

        if (runs.Select(r => r.QueryId).Distinct(StringComparer.Ordinal).Count() > 1)
        {
            throw new ArgumentException("All runs must belong to the same query.", nameof(runs));
        }

        return new CategoryInferenceQueryAggregate(
            runs[0].QueryId,
            runs.Count,
            CountAcrossRuns(runs, r => r.Emitted),
            CountAcrossRuns(runs, r => r.ExpectedMissed),
            CountAcrossRuns(runs, r => r.Unexpected),
            runs.SelectMany(r => r.UnreachableExpected)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(c => c, StringComparer.Ordinal)
                .ToList(),
            runs.Average(r => r.Precision),
            runs.Average(r => r.Recall),
            runs.Average(r => r.ReachableRecall),
            runs.Average(r => r.F1),
            runs.Min(r => r.F1),
            runs.Max(r => r.F1));
    }

    private static List<CategoryRunCount> CountAcrossRuns(
        IReadOnlyList<CategoryInferenceScore> runs,
        Func<CategoryInferenceScore, IReadOnlyList<string>> select)
    {
        return runs
            .SelectMany(select)
            .GroupBy(c => c, StringComparer.OrdinalIgnoreCase)
            .Select(g => new CategoryRunCount(g.First(), g.Count()))
            .OrderByDescending(c => c.Runs)
            .ThenBy(c => c.Category, StringComparer.Ordinal)
            .ToList();
    }
}
