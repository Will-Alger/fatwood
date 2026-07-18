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
}
