using System.Text.Json;
using ResearchDiscovery.Infrastructure.Analysis;
using Xunit;

namespace ResearchDiscovery.UnitTests.Analysis;

/// <summary>
/// Guards the structured-outputs contract: the schema must stay valid JSON,
/// keep every property required (a structured-outputs requirement for
/// additionalProperties: false), and keep composite_score present because
/// the analyzer denormalizes it for sorting.
/// </summary>
public class AnalysisContractTests
{
    [Fact]
    public void Schema_IsValidJson_WithConsistentRequiredProperties()
    {
        using var doc = JsonDocument.Parse(AnalysisContract.SchemaJson);
        var root = doc.RootElement;

        Assert.Equal("object", root.GetProperty("type").GetString());
        Assert.False(root.GetProperty("additionalProperties").GetBoolean());

        var required = root.GetProperty("required").EnumerateArray()
            .Select(r => r.GetString()!)
            .ToHashSet();
        var properties = root.GetProperty("properties").EnumerateObject()
            .Select(p => p.Name)
            .ToHashSet();

        Assert.Equal(properties, required);
        Assert.Contains("composite_score", required);
    }

    [Fact]
    public void Schema_UsesNoUnsupportedNumericConstraints()
    {
        // Structured outputs reject minimum/maximum/minLength/maxLength;
        // ranges must live in descriptions only.
        foreach (var forbidden in new[] { "\"minimum\"", "\"maximum\"", "\"minLength\"", "\"maxLength\"" })
        {
            Assert.DoesNotContain(forbidden, AnalysisContract.SchemaJson);
        }
    }

    [Fact]
    public void UserPrompt_ContainsAllPaperFields()
    {
        var prompt = AnalysisContract.BuildUserPrompt(
            "2501.12345",
            "A Great Paper",
            "Ada Lovelace; Alan Turing",
            "cs.LG",
            ["cs.LG", "q-fin.TR"],
            new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero),
            "We prove things.");

        Assert.Contains("2501.12345", prompt);
        Assert.Contains("A Great Paper", prompt);
        Assert.Contains("Ada Lovelace; Alan Turing", prompt);
        Assert.Contains("cs.LG, q-fin.TR", prompt);
        Assert.Contains("2026-06-01", prompt);
        Assert.Contains("We prove things.", prompt);
    }
}
