using ResearchDiscovery.Infrastructure.Arxiv;
using Xunit;

namespace ResearchDiscovery.UnitTests.Arxiv;

/// <summary>
/// Guards <see cref="ArxivCategoryNames.AliasGroups"/>, which derives alias
/// twins from shared display names rather than from a hand-kept list. That
/// derivation is self-maintaining in one direction — a pair added to the
/// taxonomy is picked up for free — and fragile in the other: giving two
/// genuinely distinct fields the same display name silently makes them twins,
/// and everything downstream (the eval fixture guard below, the category UI)
/// would then treat one field as the other. These pin both directions.
/// </summary>
public class ArxivCategoryAliasTests
{
    /// <summary>
    /// The alias pairs arXiv actually publishes, as of the taxonomy in
    /// <see cref="ArxivCategoryNames"/>. Written out here — and nowhere in the
    /// production path — so that a group appearing, vanishing or growing is a
    /// test failure a human reads, not a silent change in behaviour.
    /// </summary>
    private static readonly string[][] KnownGroups =
    [
        ["cs.IT", "math.IT"],
        ["cs.NA", "math.NA"],
        ["cs.SY", "eess.SY"],
        ["math-ph", "math.MP"],
        ["math.ST", "stat.TH"],
    ];

    [Fact]
    public void TheTaxonomyPublishesExactlyTheKnownAliasGroups()
    {
        var expected = KnownGroups
            .Select(group => string.Join('/', group.Order(StringComparer.Ordinal)))
            .Order(StringComparer.Ordinal);

        var actual = ArxivCategoryNames.AliasGroups
            .Select(group => string.Join('/', group))
            .Order(StringComparer.Ordinal);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void EveryAliasResolvesBothWays()
    {
        foreach (var group in KnownGroups)
        {
            foreach (var code in group)
            {
                var expected = group.Where(other => other != code).Order(StringComparer.Ordinal);
                Assert.Equal(expected, ArxivCategoryNames.AliasesFor(code));
            }
        }
    }

    [Fact]
    public void MachineLearningCodesAreNotTreatedAsTwins()
    {
        // The near miss this derivation has to survive: cs.LG and stat.ML are
        // heavily cross-listed and colloquially the same words, but they are
        // distinct codes with distinct ground truth in the eval —
        // uncertainty-quant-statistician expects stat.ML and merely forgives
        // cs.LG. Shortening stat.ML's name from "Machine Learning (Statistics)"
        // to "Machine Learning" would make them twins and quietly rewrite that
        // judgement into a tautology.
        Assert.Empty(ArxivCategoryNames.AliasesFor("cs.LG"));
        Assert.Empty(ArxivCategoryNames.AliasesFor("stat.ML"));
    }

    [Fact]
    public void ACodeTheTaxonomyDoesNotNameHasNoAliases()
    {
        // Unknown codes fall back to themselves in DisplayNameFor; two of them
        // must not become each other's twins by sharing that fallback.
        Assert.Empty(ArxivCategoryNames.AliasesFor("cs.NOTACODE"));
        Assert.Empty(ArxivCategoryNames.AliasesFor("zz.ALSONOT"));
    }
}
