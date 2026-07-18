using ResearchDiscovery.Infrastructure.Search;
using Xunit;

namespace ResearchDiscovery.UnitTests.Search;

public class SearchPlanCompilerFormatTests
{
    [Fact]
    public void FormatKnownCategories_NamesKnownCodes_SortsAndDedupes()
    {
        var formatted = AnthropicSearchPlanCompiler.FormatKnownCategories(
            ["q-bio.QM", "cs.LG", "cs.lg", "xx.YZ"]);

        var lines = formatted.Split('\n');
        Assert.Equal(3, lines.Length);
        Assert.Equal("cs.LG - Machine Learning", lines[0]);
        Assert.Equal("q-bio.QM - Quantitative Methods", lines[1]);
        // Unknown codes fall back to the code itself, never break the prompt.
        Assert.Equal("xx.YZ - xx.YZ", lines[2]);
    }
}
