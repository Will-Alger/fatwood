using ResearchDiscovery.Infrastructure.Eval;
using Xunit;

namespace ResearchDiscovery.UnitTests.Eval;

/// <summary>
/// Hand-computed values for the judge-agreement statistic. Quadratic weighted
/// kappa: 1 = perfect agreement, 0 = what chance alone would produce.
/// </summary>
public class CalibrationTests
{
    [Fact]
    public void Kappa_PerfectAgreement_IsOne()
    {
        // All mass on the diagonal.
        int[][] confusion = [[10, 0, 0, 0], [0, 10, 0, 0], [0, 0, 10, 0], [0, 0, 0, 10]];

        Assert.Equal(1.0, EvalRunner.QuadraticWeightedKappa(confusion, 40), 6);
    }

    [Fact]
    public void Kappa_IndependentJudges_IsZero()
    {
        // Uniform grid: observed disagreement equals chance expectation exactly.
        int[][] confusion = [[1, 1, 1, 1], [1, 1, 1, 1], [1, 1, 1, 1], [1, 1, 1, 1]];

        Assert.Equal(0.0, EvalRunner.QuadraticWeightedKappa(confusion, 16), 6);
    }

    [Fact]
    public void Kappa_OffByOneErrors_BeatOffByThreeErrors()
    {
        // Same number of disagreements; quadratic weighting must punish the
        // far-off ones much harder.
        int[][] nearMisses = [[8, 2, 0, 0], [0, 10, 0, 0], [0, 0, 10, 0], [0, 0, 0, 10]];
        int[][] farMisses = [[8, 0, 0, 2], [0, 10, 0, 0], [0, 0, 10, 0], [0, 0, 0, 10]];

        var near = EvalRunner.QuadraticWeightedKappa(nearMisses, 40);
        var far = EvalRunner.QuadraticWeightedKappa(farMisses, 40);

        Assert.True(near > far);
        Assert.InRange(near, 0.9, 1.0);
    }

    [Fact]
    public void Kappa_EmptySample_IsZero()
    {
        int[][] confusion = [[0, 0, 0, 0], [0, 0, 0, 0], [0, 0, 0, 0], [0, 0, 0, 0]];

        Assert.Equal(0.0, EvalRunner.QuadraticWeightedKappa(confusion, 0));
    }
}
