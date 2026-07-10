using ResearchDiscovery.Application.Abstractions;
using ResearchDiscovery.Infrastructure.Enrichment;
using ResearchDiscovery.Infrastructure.Search;
using Xunit;

namespace ResearchDiscovery.UnitTests.Search;

/// <summary>Pure logic of the staged retrieval pipeline.</summary>
public class RetrievalStagesTests
{
    [Fact]
    public void SplitAnchors_SplitsTopicListAndCaps()
    {
        var anchors = SearchService.SplitAnchors(
            "algorithmic trading, portfolio optimization, market microstructure");

        Assert.Equal(
            ["algorithmic trading", "portfolio optimization", "market microstructure"],
            anchors);
    }

    [Fact]
    public void SplitAnchors_FallsBackToWholeTextWhenNotAList()
    {
        var anchors = SearchService.SplitAnchors("just one topic without separators");

        Assert.Equal(["just one topic without separators"], anchors);
    }

    [Fact]
    public void TeamDraftInterleave_AlternatesTeamsAndNeverDuplicates()
    {
        List<ScoredPaper> control = [new(1, 0.9f), new(2, 0.8f), new(3, 0.7f)];
        List<ScoredPaper> candidate = [new(2, 0.95f), new(4, 0.85f), new(5, 0.75f)];

        // Deterministic coin: A always drafts first.
        var interleaved = SearchService.TeamDraftInterleave(
            control, candidate, limit: 4, coinFlip: () => true);

        Assert.Equal(4, interleaved.Count);
        Assert.Equal([1L, 2L, 3L, 4L], interleaved.Select(i => i.Paper.PaperId));
        Assert.Equal(["A", "B", "A", "B"], interleaved.Select(i => i.Variant));
        Assert.Equal(
            interleaved.Count,
            interleaved.Select(i => i.Paper.PaperId).Distinct().Count());
    }

    [Fact]
    public void Tokenize_LowercasesAndDropsShortTokens()
    {
        var tokens = InMemoryLexicalIndex.Tokenize("Limit Order-Book (LOB) dynamics, a 2-step model!")
            .ToList();

        Assert.Equal(
            ["limit", "order", "book", "lob", "dynamics", "step", "model"],
            tokens);
    }

    [Theory]
    [InlineData("https://github.com/owner/repo", "owner/repo")]
    [InlineData("https://github.com/owner/repo.git", "owner/repo")]
    [InlineData("https://github.com/owner/repo/tree/main/src", "owner/repo")]
    [InlineData("See code at github.com/owner/repo.", "owner/repo")]
    [InlineData("https://gitlab.com/owner/repo", null)]
    [InlineData("https://github.com/owner", null)]
    public void ParseGitHubRepo_ExtractsOwnerAndRepo(string url, string? expected)
    {
        Assert.Equal(expected, PaperSignalEnricher.ParseGitHubRepo(url));
    }
}
