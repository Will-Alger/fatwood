namespace ResearchDiscovery.Application.Eval;

/// <summary>
/// Standard offline information-retrieval metrics, computed against graded
/// relevance judgments (0=irrelevant .. 3=excellent). Unjudged documents are
/// treated as grade 0 — the standard pooled-evaluation assumption — so scores
/// are only meaningful when the judgment pool covers the ranker's head.
/// Metrics return null when a query has no judged-relevant documents at all
/// (the metric is undefined, not zero).
/// </summary>
public static class RankingMetrics
{
    /// <summary>Grade at or above which a document counts as "relevant" for recall/MRR.</summary>
    public const int RelevantThreshold = 2;

    /// <summary>
    /// Normalized discounted cumulative gain over the first <paramref name="k"/>
    /// ranks, with exponential gain (2^grade − 1) so a grade-3 paper at rank 1
    /// is worth far more than three grade-1 papers.
    /// </summary>
    public static double? NdcgAtK(
        IReadOnlyList<string> ranked, IReadOnlyDictionary<string, int> grades, int k)
    {
        var ideal = grades.Values
            .Where(g => g > 0)
            .OrderByDescending(g => g)
            .Take(k)
            .Select((g, i) => Gain(g) / Discount(i))
            .Sum();

        if (ideal == 0)
        {
            return null;
        }

        var dcg = ranked
            .Take(k)
            .Select((id, i) => Gain(grades.GetValueOrDefault(id)) / Discount(i))
            .Sum();

        return dcg / ideal;
    }

    /// <summary>
    /// Fraction of all judged-relevant documents (grade ≥ threshold) that
    /// appear in the first <paramref name="k"/> ranks.
    /// </summary>
    public static double? RecallAtK(
        IReadOnlyList<string> ranked, IReadOnlyDictionary<string, int> grades, int k)
    {
        var relevant = grades
            .Where(g => g.Value >= RelevantThreshold)
            .Select(g => g.Key)
            .ToHashSet(StringComparer.Ordinal);

        if (relevant.Count == 0)
        {
            return null;
        }

        var found = ranked.Take(k).Count(relevant.Contains);
        return (double)found / relevant.Count;
    }

    /// <summary>
    /// Reciprocal rank of the first relevant result (1 for rank 1, 0.5 for
    /// rank 2, ...); 0 when no relevant document was returned at all.
    /// </summary>
    public static double? ReciprocalRank(
        IReadOnlyList<string> ranked, IReadOnlyDictionary<string, int> grades)
    {
        if (!grades.Values.Any(g => g >= RelevantThreshold))
        {
            return null;
        }

        for (var i = 0; i < ranked.Count; i++)
        {
            if (grades.GetValueOrDefault(ranked[i]) >= RelevantThreshold)
            {
                return 1.0 / (i + 1);
            }
        }

        return 0;
    }

    private static double Gain(int grade) => Math.Pow(2, grade) - 1;

    private static double Discount(int zeroBasedRank) => Math.Log2(zeroBasedRank + 2);
}
