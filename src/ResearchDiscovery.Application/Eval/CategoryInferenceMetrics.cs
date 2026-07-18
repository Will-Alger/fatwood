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

public sealed record CategoryInferenceReport(
    IReadOnlyList<CategoryInferenceScore> Queries,
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
}
