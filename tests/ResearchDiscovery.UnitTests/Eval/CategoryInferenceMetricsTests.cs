using ResearchDiscovery.Application.Eval;
using Xunit;

namespace ResearchDiscovery.UnitTests.Eval;

public class CategoryInferenceMetricsTests
{
    private static readonly string[] Known =
        ["cs.LG", "cs.SD", "eess.AS", "eess.SP", "q-bio.QM", "cs.DC", "cs.DB"];

    [Fact]
    public void ExactMatch_IsPerfect()
    {
        var s = CategoryInferenceMetrics.Score(
            "q", ["cs.SD", "eess.AS"], ["cs.SD", "eess.AS"], null, Known);

        Assert.Equal(1.0, s.Precision);
        Assert.Equal(1.0, s.Recall);
        Assert.Equal(1.0, s.F1);
        Assert.Empty(s.ExpectedMissed);
        Assert.Empty(s.Unexpected);
    }

    [Fact]
    public void MissingExpected_HalvesRecall_KeepsPrecision()
    {
        var s = CategoryInferenceMetrics.Score(
            "q", ["cs.SD"], ["cs.SD", "eess.AS"], null, Known);

        Assert.Equal(1.0, s.Precision);
        Assert.Equal(0.5, s.Recall);
        Assert.Equal(["eess.AS"], s.ExpectedMissed);
    }

    [Fact]
    public void AcceptableExtras_DoNotHurtPrecision()
    {
        var s = CategoryInferenceMetrics.Score(
            "q", ["cs.SD", "eess.SP"], ["cs.SD"], ["eess.SP"], Known);

        Assert.Equal(1.0, s.Precision);
        Assert.Empty(s.Unexpected);
    }

    [Fact]
    public void OffTargetExtras_HurtPrecision_AndAreListed()
    {
        var s = CategoryInferenceMetrics.Score(
            "q", ["cs.SD", "cs.DB"], ["cs.SD"], null, Known);

        Assert.Equal(0.5, s.Precision);
        Assert.Equal(["cs.DB"], s.Unexpected);
    }

    [Fact]
    public void EmptyExpected_EmptyEmitted_IsPerfect()
    {
        var s = CategoryInferenceMetrics.Score("q", [], [], null, Known);

        Assert.Equal(1.0, s.Precision);
        Assert.Equal(1.0, s.Recall);
        Assert.Equal(1.0, s.F1);
    }

    [Fact]
    public void EmptyExpected_NarrowedAnyway_IsAPrecisionError()
    {
        var s = CategoryInferenceMetrics.Score("q", ["cs.LG"], [], null, Known);

        Assert.Equal(0.0, s.Precision);
        Assert.Equal(1.0, s.Recall);
    }

    [Fact]
    public void ExpectedSlice_ButEmittedNothing_IsARecallError()
    {
        var s = CategoryInferenceMetrics.Score("q", [], ["cs.DC", "cs.DB"], null, Known);

        Assert.Equal(1.0, s.Precision);
        Assert.Equal(0.0, s.Recall);
        Assert.Equal(0.0, s.F1);
    }

    [Fact]
    public void Matching_IsCaseInsensitive_AndDedupes()
    {
        var s = CategoryInferenceMetrics.Score(
            "q", ["CS.sd", "cs.SD"], ["cs.SD"], null, Known);

        Assert.Equal(1.0, s.Precision);
        Assert.Equal(1.0, s.Recall);
        Assert.Single(s.Emitted);
    }

    [Fact]
    public void UnreachableExpected_IsReported_AndExcludedFromReachableRecall()
    {
        // eess.IV is not in the taxonomy yet: raw recall counts the miss,
        // reachable recall (and F1) attribute only what the compiler could do.
        var s = CategoryInferenceMetrics.Score(
            "q", ["q-bio.QM"], ["q-bio.QM", "eess.IV"], null, Known);

        Assert.Equal(["eess.IV"], s.UnreachableExpected);
        Assert.Equal(0.5, s.Recall);
        Assert.Equal(1.0, s.ReachableRecall);
        Assert.Equal(1.0, s.F1);
    }

    [Fact]
    public void AllExpectedUnreachable_ReachableRecallDefaultsToPerfect()
    {
        var s = CategoryInferenceMetrics.Score(
            "q", [], ["eess.IV"], null, Known);

        Assert.Equal(0.0, s.Recall);
        Assert.Equal(1.0, s.ReachableRecall);
    }

    [Fact]
    public void Aggregate_SingleRun_PassesMetricsThrough()
    {
        var s = CategoryInferenceMetrics.Score(
            "q", ["cs.SD"], ["cs.SD", "eess.AS"], null, Known);
        var a = CategoryInferenceMetrics.Aggregate([s]);

        Assert.Equal(1, a.Runs);
        Assert.Equal(s.Precision, a.MeanPrecision);
        Assert.Equal(s.Recall, a.MeanRecall);
        Assert.Equal(s.F1, a.MeanF1);
        Assert.Equal(s.F1, a.MinF1);
        Assert.Equal(s.F1, a.MaxF1);
        Assert.Equal([new CategoryRunCount("cs.SD", 1)], a.Emitted);
        Assert.Equal([new CategoryRunCount("eess.AS", 1)], a.ExpectedMissed);
    }

    [Fact]
    public void Aggregate_AveragesMetrics_AndTracksSpread()
    {
        // Run 1 hits both expected codes; run 2 misses one and adds an
        // off-target extra — the classic unstable-compile shape.
        var run1 = CategoryInferenceMetrics.Score(
            "q", ["cs.SD", "eess.AS"], ["cs.SD", "eess.AS"], null, Known);
        var run2 = CategoryInferenceMetrics.Score(
            "q", ["cs.SD", "cs.DB"], ["cs.SD", "eess.AS"], null, Known);
        var a = CategoryInferenceMetrics.Aggregate([run1, run2]);

        Assert.Equal(2, a.Runs);
        Assert.Equal((run1.Precision + run2.Precision) / 2, a.MeanPrecision, 10);
        Assert.Equal((run1.Recall + run2.Recall) / 2, a.MeanRecall, 10);
        Assert.Equal(run2.F1, a.MinF1);
        Assert.Equal(run1.F1, a.MaxF1);
        // Stable emission counted twice, unstable ones once; ordered by
        // count desc then name.
        Assert.Equal(
            [new CategoryRunCount("cs.SD", 2), new CategoryRunCount("cs.DB", 1), new CategoryRunCount("eess.AS", 1)],
            a.Emitted);
        Assert.Equal([new CategoryRunCount("eess.AS", 1)], a.ExpectedMissed);
        Assert.Equal([new CategoryRunCount("cs.DB", 1)], a.Unexpected);
    }

    [Fact]
    public void Aggregate_MergesEmissionsCaseInsensitively()
    {
        var run1 = CategoryInferenceMetrics.Score("q", ["CS.sd"], ["cs.SD"], null, Known);
        var run2 = CategoryInferenceMetrics.Score("q", ["cs.SD"], ["cs.SD"], null, Known);
        var a = CategoryInferenceMetrics.Aggregate([run1, run2]);

        var only = Assert.Single(a.Emitted);
        Assert.Equal(2, only.Runs);
    }

    [Fact]
    public void Aggregate_RejectsEmptyAndMixedQueries()
    {
        var s1 = CategoryInferenceMetrics.Score("a", [], [], null, Known);
        var s2 = CategoryInferenceMetrics.Score("b", [], [], null, Known);

        Assert.Throws<ArgumentException>(() => CategoryInferenceMetrics.Aggregate([]));
        Assert.Throws<ArgumentException>(() => CategoryInferenceMetrics.Aggregate([s1, s2]));
    }
}
