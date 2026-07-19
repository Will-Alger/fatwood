using ResearchDiscovery.Application.Abstractions;
using ResearchDiscovery.Infrastructure.Search;
using Xunit;

namespace ResearchDiscovery.UnitTests.Search;

/// <summary>
/// The wildcard slots are contractual (docs/search-quality.md): the final two
/// result slots belong to the least experience-similar papers still in the
/// high-relevance pool, so the user's comfort zone can't silently narrow what
/// they see.
/// </summary>
public class WildcardSelectionTests
{
    private static List<ScoredPaper> Pool(int count) =>
        [.. Enumerable.Range(1, count).Select(i => new ScoredPaper(i, 1f - (i * 0.01f)))];

    [Fact]
    public void SelectWithWildcards_ReservesFinalSlotsForLeastExperienceSimilar()
    {
        var pool = Pool(10);

        // Papers 9 and 7 are the farthest from the user's experience.
        var experience = new Dictionary<long, float>
        {
            [1] = 0.9f, [2] = 0.9f, [3] = 0.9f, [4] = 0.8f, [5] = 0.7f,
            [6] = 0.6f, [7] = 0.10f, [8] = 0.5f, [9] = 0.05f, [10] = 0.4f,
        };

        var selection = SearchService.SelectWithWildcards(pool, limit: 5, experience);

        Assert.Equal(5, selection.Count);
        Assert.Equal([1L, 2L, 3L], selection.Take(3).Select(s => s.Paper.PaperId));
        Assert.All(selection.Take(3), s => Assert.False(s.IsWildcard));
        Assert.Equal([9L, 7L], selection.Skip(3).Select(s => s.Paper.PaperId));
        Assert.All(selection.Skip(3), s => Assert.True(s.IsWildcard));
    }

    [Fact]
    public void SelectWithWildcards_WithoutProfile_ReturnsPlainTopN()
    {
        var selection = SearchService.SelectWithWildcards(Pool(10), limit: 5, null);

        Assert.Equal([1L, 2L, 3L, 4L, 5L], selection.Select(s => s.Paper.PaperId));
        Assert.All(selection, s => Assert.False(s.IsWildcard));
    }

    [Fact]
    public void SelectWithWildcards_PoolNoLargerThanLimit_HasNothingToSwapIn()
    {
        var experience = Pool(5).ToDictionary(p => p.PaperId, _ => 0.5f);

        var selection = SearchService.SelectWithWildcards(Pool(5), limit: 5, experience);

        Assert.Equal(5, selection.Count);
        Assert.All(selection, s => Assert.False(s.IsWildcard));
    }

    [Fact]
    public void SelectWithWildcards_LimitAtOrBelowSlotCount_NeverGoesAllWildcard()
    {
        var experience = Pool(10).ToDictionary(p => p.PaperId, _ => 0.5f);

        var selection = SearchService.SelectWithWildcards(Pool(10), limit: 2, experience);

        Assert.Equal([1L, 2L], selection.Select(s => s.Paper.PaperId));
        Assert.All(selection, s => Assert.False(s.IsWildcard));
    }

    [Fact]
    public void SelectWithWildcards_PapersWithoutAnExperienceScoreSortLast()
    {
        var pool = Pool(10);

        // Only paper 8 has a known (low) experience score; unscored papers
        // sort after every scored one, so the second slot falls back to the
        // first unscored tail paper in rank order.
        var experience = new Dictionary<long, float> { [8] = 0.1f };

        var selection = SearchService.SelectWithWildcards(pool, limit: 5, experience);

        Assert.Equal([8L, 4L], selection.Skip(3).Select(s => s.Paper.PaperId));
        Assert.All(selection.Skip(3), s => Assert.True(s.IsWildcard));
    }
}
