using ResearchDiscovery.Application.Eval;
using Xunit;

namespace ResearchDiscovery.UnitTests.Eval;

/// <summary>
/// Hand-computed known values for the offline metrics. If these move, every
/// historical eval baseline becomes incomparable — treat any change here as a
/// breaking change to the harness.
/// </summary>
public class RankingMetricsTests
{
    private static readonly Dictionary<string, int> Grades = new()
    {
        ["A"] = 3,
        ["B"] = 2,
        ["C"] = 1,
    };

    [Fact]
    public void Ndcg_PerfectRanking_IsOne()
    {
        var ndcg = RankingMetrics.NdcgAtK(["A", "B", "C"], Grades, 10);

        Assert.NotNull(ndcg);
        Assert.Equal(1.0, ndcg.Value, precision: 10);
    }

    [Fact]
    public void Ndcg_SwappedTopTwo_MatchesHandComputedValue()
    {
        // ranked: B(2), A(3), X(unjudged), C(1) at k=3.
        // DCG  = 3/log2(2) + 7/log2(3) + 0/log2(4)
        // IDCG = 7/log2(2) + 3/log2(3) + 1/log2(4)
        var expected =
            (3.0 / 1 + 7.0 / Math.Log2(3)) /
            (7.0 / 1 + 3.0 / Math.Log2(3) + 1.0 / 2);

        var ndcg = RankingMetrics.NdcgAtK(["B", "A", "X", "C"], Grades, 3);

        Assert.NotNull(ndcg);
        Assert.Equal(expected, ndcg.Value, precision: 10);
    }

    [Fact]
    public void Ndcg_UnjudgedDocumentsScoreZeroGain()
    {
        // Only A(3) judged; it lands at rank 3: DCG = 7/log2(4) = 3.5, IDCG = 7.
        var ndcg = RankingMetrics.NdcgAtK(
            ["X", "Y", "A"], new Dictionary<string, int> { ["A"] = 3 }, 3);

        Assert.NotNull(ndcg);
        Assert.Equal(0.5, ndcg.Value, precision: 10);
    }

    [Fact]
    public void Ndcg_NoJudgedRelevantDocuments_IsUndefined()
    {
        var ndcg = RankingMetrics.NdcgAtK(["X", "Y"], new Dictionary<string, int> { ["X"] = 0 }, 10);

        Assert.Null(ndcg);
    }

    [Fact]
    public void Recall_CountsOnlyGradesAtOrAboveThreshold()
    {
        var grades = new Dictionary<string, int>
        {
            ["A"] = 3,
            ["B"] = 2,
            ["C"] = 1, // below threshold — not part of the recall base
            ["D"] = 2,
        };

        Assert.Equal(1.0 / 3, RankingMetrics.RecallAtK(["A", "C", "X", "B"], grades, 2)!.Value, 10);
        Assert.Equal(2.0 / 3, RankingMetrics.RecallAtK(["A", "C", "X", "B"], grades, 4)!.Value, 10);
    }

    [Fact]
    public void Recall_NoRelevantJudged_IsUndefined()
    {
        Assert.Null(RankingMetrics.RecallAtK(["C"], new Dictionary<string, int> { ["C"] = 1 }, 10));
    }

    [Fact]
    public void ReciprocalRank_FirstRelevantAtRankThree_IsOneThird()
    {
        Assert.Equal(1.0 / 3, RankingMetrics.ReciprocalRank(["X", "C", "B"], Grades)!.Value, 10);
    }

    [Fact]
    public void ReciprocalRank_RelevantExistsButNotReturned_IsZero()
    {
        Assert.Equal(0, RankingMetrics.ReciprocalRank(["X", "C"], Grades)!.Value, 10);
    }

    [Fact]
    public void ReciprocalRank_NoRelevantJudged_IsUndefined()
    {
        Assert.Null(RankingMetrics.ReciprocalRank(["X"], new Dictionary<string, int> { ["X"] = 1 }));
    }
}
