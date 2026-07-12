using ResearchDiscovery.Infrastructure.Search;
using Xunit;

namespace ResearchDiscovery.UnitTests.Search;

/// <summary>Compiler output → SearchPlan parsing, including the HyDE field.</summary>
public class PlanParsingTests
{
    private static readonly string[] KnownCategories = ["cs.LG", "cs.AI", "q-fin.CP"];

    [Fact]
    public void ParsePlan_ReadsHypotheticalAbstract()
    {
        const string json = """
        {
          "interpretation": "You want X.",
          "anchor_text": "topic one, topic two",
          "categories": ["cs.LG", "made.UP"],
          "date_window_days": 90,
          "require_no_code": null,
          "hypothetical_abstract": "We present a method for X that improves Y."
        }
        """;

        var plan = AnthropicSearchPlanCompiler.ParsePlan(json, KnownCategories);

        Assert.Equal("We present a method for X that improves Y.", plan.HypotheticalAbstract);
        Assert.Equal(["cs.LG"], plan.Categories); // unknown codes still filtered
        Assert.Equal(90, plan.DateWindowDays);
    }

    [Theory]
    [InlineData("""{"interpretation":"i","anchor_text":"a","categories":[],"date_window_days":null,"require_no_code":null}""")]
    [InlineData("""{"interpretation":"i","anchor_text":"a","categories":[],"date_window_days":null,"require_no_code":null,"hypothetical_abstract":""}""")]
    [InlineData("""{"interpretation":"i","anchor_text":"a","categories":[],"date_window_days":null,"require_no_code":null,"hypothetical_abstract":"   "}""")]
    public void ParsePlan_MissingOrBlankHydeIsNull(string json)
    {
        // Plans logged before the field existed (and blank model output) must
        // deserialize to null so retrieval cleanly skips the extra anchor.
        var plan = AnthropicSearchPlanCompiler.ParsePlan(json, KnownCategories);

        Assert.Null(plan.HypotheticalAbstract);
    }
}
