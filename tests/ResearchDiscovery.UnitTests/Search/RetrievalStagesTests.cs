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
    public void SelectWithWildcards_ReservesFinalSlotsForLeastExperienceSimilar()
    {
        List<ScoredPaper> pool =
            [new(1, 0.9f), new(2, 0.8f), new(3, 0.7f), new(4, 0.6f), new(5, 0.5f), new(6, 0.4f)];
        var experience = new Dictionary<long, float>
        {
            [1] = 0.9f, [2] = 0.8f, [3] = 0.7f, [4] = 0.05f, [5] = 0.6f, [6] = 0.1f,
        };

        var selection = SearchService.SelectWithWildcards(pool, limit: 4, experience);

        Assert.Equal(4, selection.Count);
        // Top slots stay pure relevance order, never reordered by experience.
        Assert.Equal([1L, 2L], selection.Take(2).Select(s => s.Paper.PaperId));
        Assert.All(selection.Take(2), s => Assert.False(s.IsWildcard));
        // Final slots: the least experience-similar of the rest of the pool.
        Assert.Equal([4L, 6L], selection.Skip(2).Select(s => s.Paper.PaperId));
        Assert.All(selection.Skip(2), s => Assert.True(s.IsWildcard));
    }

    [Fact]
    public void SelectWithWildcards_NoExperienceScores_ReturnsPlainTopN()
    {
        List<ScoredPaper> pool =
            [new(1, 0.9f), new(2, 0.8f), new(3, 0.7f), new(4, 0.6f), new(5, 0.5f)];

        var selection = SearchService.SelectWithWildcards(pool, limit: 4, experienceScores: null);

        Assert.Equal([1L, 2L, 3L, 4L], selection.Select(s => s.Paper.PaperId));
        Assert.All(selection, s => Assert.False(s.IsWildcard));
    }

    [Fact]
    public void SelectWithWildcards_PoolNoBiggerThanLimit_HasNoWildcards()
    {
        List<ScoredPaper> pool = [new(1, 0.9f), new(2, 0.8f), new(3, 0.7f), new(4, 0.6f)];
        var experience = new Dictionary<long, float> { [1] = 0.9f, [2] = 0.1f, [3] = 0.2f, [4] = 0.3f };

        var selection = SearchService.SelectWithWildcards(pool, limit: 4, experience);

        Assert.Equal(4, selection.Count);
        Assert.All(selection, s => Assert.False(s.IsWildcard));
    }

    [Fact]
    public void SelectWithWildcards_UnscoredPapersAreLastPickWildcards()
    {
        List<ScoredPaper> pool =
            [new(1, 0.9f), new(2, 0.8f), new(3, 0.7f), new(4, 0.6f), new(5, 0.5f), new(6, 0.4f)];
        // Papers 5 and 6 have no experience score (e.g. missing vectors):
        // they must sort AFTER scored papers, not win the wildcard slots.
        var experience = new Dictionary<long, float> { [3] = 0.3f, [4] = 0.2f };

        var selection = SearchService.SelectWithWildcards(pool, limit: 4, experience);

        Assert.Equal([4L, 3L], selection.Skip(2).Select(s => s.Paper.PaperId));
        Assert.All(selection.Skip(2), s => Assert.True(s.IsWildcard));
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
