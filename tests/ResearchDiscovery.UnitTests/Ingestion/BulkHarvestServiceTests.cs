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
        // Some arXiv archives (e.g. econ before subdivision, "math-ph") have
        // no dotted subcategory; the code is already the set.
        var sets = BulkHarvestService.DeriveSets(["math-ph", "cs.AI"]);

        Assert.Equal(["cs", "math-ph"], sets);
    }

    [Fact]
    public void DeriveSets_EmptyInput_YieldsNoSets()
    {
        Assert.Empty(BulkHarvestService.DeriveSets([]));
    }
}
