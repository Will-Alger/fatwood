using ResearchDiscovery.Infrastructure.Ingestion;
using Xunit;

namespace ResearchDiscovery.UnitTests.Ingestion;

public class BulkHarvestServiceTests
{
    [Fact]
    public void DeriveSets_MapsCategoryCodesToDistinctOrderedArchives()
    {
        var sets = BulkHarvestService.DeriveSets(["q-fin.CP", "cs.LG", "cs.CV", "cs.LG"]);

        Assert.Equal(["cs", "q-fin"], sets);
    }

    [Fact]
    public void DeriveSets_PassesThroughDotlessArchiveCodes()
    {
        // Dotless archive codes pass through as their own set — except
        // physics-group archives, which map to their scoped OAI set names.
        var sets = BulkHarvestService.DeriveSets(["math-ph", "econ", "cs.AI"]);

        Assert.Equal(["cs", "econ", "physics:math-ph"], sets);
    }

    [Fact]
    public void DeriveSets_EmptyInput_YieldsNoSets()
    {
        Assert.Empty(BulkHarvestService.DeriveSets([]));
    }
    [Fact]
    public void DeriveSets_MapsPhysicsGroupArchivesToScopedSets()
    {
        var sets = BulkHarvestService.DeriveSets(
            ["physics.comp-ph", "astro-ph.IM", "math.OC", "cs.LG"]);

        Assert.Equal(["cs", "math", "physics:astro-ph", "physics:physics"], sets);
    }
}
