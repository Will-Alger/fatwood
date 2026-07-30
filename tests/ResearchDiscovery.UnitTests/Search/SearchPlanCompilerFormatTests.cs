using System.Text.Json;
using ResearchDiscovery.Application.Options;
using ResearchDiscovery.Infrastructure.Search;
using Xunit;

namespace ResearchDiscovery.UnitTests.Search;

public class SearchPlanCompilerFormatTests
{
    [Fact]
    public void BuildSchemaJson_Defaults_ReproduceTheShippedPrompt()
    {
        var schema = AnthropicSearchPlanCompiler.BuildSchemaJson(new SearchCompilerOptions());

        // The two configurable ranges must render exactly as the prompt has
        // always shipped — a default-config deploy changes nothing.
        Assert.Contains("list of 8-15 concrete research topics", schema);
        Assert.Contains("search, 4-6 sentences, written exactly like", schema);

        using var doc = JsonDocument.Parse(schema);
        Assert.Equal(7, doc.RootElement.GetProperty("required").GetArrayLength());
    }

    [Fact]
    public void BuildSchemaJson_CustomRanges_AreInterpolated()
    {
        var schema = AnthropicSearchPlanCompiler.BuildSchemaJson(new SearchCompilerOptions
        {
            MinTopics = 6,
            MaxTopics = 10,
            HydeMinSentences = 2,
            HydeMaxSentences = 3,
        });

        Assert.Contains("list of 6-10 concrete research topics", schema);
        Assert.Contains("search, 2-3 sentences, written exactly like", schema);
        using var doc = JsonDocument.Parse(schema); // still valid JSON
    }

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
