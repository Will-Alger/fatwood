using ResearchDiscovery.Application.Eval;
using ResearchDiscovery.Infrastructure.Arxiv;
using ResearchDiscovery.Infrastructure.Eval;
using Xunit;

namespace ResearchDiscovery.UnitTests.Eval;

/// <summary>
/// Guards the authored ground truth in eval/queries.json. `eval categories`
/// reports whatever the fixture claims, so an authoring slip is invisible in
/// its output: a typo'd expectation is silently reported as a corpus gap
/// (UnreachableExpected), and a typo'd acceptable code silently stops
/// forgiving the extra it was written to forgive. Both are cheap to catch at
/// PR time, which is what these do.
/// </summary>
public class EvalQueryFixtureTests
{
    private static readonly EvalQuerySet Set = EvalFileStore.LoadQueries(
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "eval-queries.json"));

    private static bool IsKnownCategory(string code) =>
        ArxivCategoryNames.DisplayNameFor(code) != code;

    [Fact]
    public void QueryIdsAreUniqueAndPopulated()
    {
        Assert.NotEmpty(Set.Queries);
        Assert.All(Set.Queries, q =>
        {
            Assert.False(string.IsNullOrWhiteSpace(q.Id));
            Assert.False(string.IsNullOrWhiteSpace(q.Query));
            Assert.False(string.IsNullOrWhiteSpace(q.Persona));
        });

        var duplicates = Set.Queries
            .GroupBy(q => q.Id, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();

        Assert.Empty(duplicates);
    }

    [Fact]
    public void EveryAuthoredCategoryCodeIsInTheArxivTaxonomy()
    {
        var unknown = Set.Queries
            .SelectMany(q => (q.ExpectedCategories ?? [])
                .Concat(q.AcceptableCategories ?? [])
                .Concat(q.Plan?.Categories ?? [])
                .Where(c => !IsKnownCategory(c))
                .Select(c => $"{q.Id}: {c}"))
            .ToList();

        Assert.Empty(unknown);
    }

    [Fact]
    public void ExpectedAndAcceptableCategoriesDoNotOverlap()
    {
        // A code is either a must-have or a tolerated extra. Listing it as both
        // scores the same (the allowed set is their union) but means the author
        // did not decide, and the next reader cannot tell which was meant.
        var overlapping = Set.Queries
            .Where(q => q.ExpectedCategories is not null && q.AcceptableCategories is not null)
            .SelectMany(q => q.ExpectedCategories!
                .Intersect(q.AcceptableCategories!, StringComparer.OrdinalIgnoreCase)
                .Select(c => $"{q.Id}: {c}"))
            .ToList();

        Assert.Empty(overlapping);
    }

    [Fact]
    public void AliasPairsAreNeverSplitAcrossAllowedAndAbsent()
    {
        // arXiv files a few fields under two codes at once (cs.SY/eess.SY,
        // cs.NA/math.NA, ...) and the taxonomy gives each pair one display
        // name because it is one field. The compiler may legitimately answer
        // with either spelling, so authoring one spelling while leaving the
        // other out of BOTH lists measures the author's choice of code rather
        // than the compiler's choice of field: naming the twin then costs
        // recall if the authored code was expected, and precision either way.
        //
        // The rule is therefore about the allowed set as a whole — if a query
        // mentions either half of a pair, it must allow both halves. Which
        // half is the must-have stays the author's call; `cfd-solver-portfolio`
        // expecting math.NA with cs.NA merely acceptable satisfies this.
        //
        // Frozen plans are deliberately out of scope: those record what the
        // compiler actually emitted, not what the author is asking for.
        var split = Set.Queries
            .SelectMany(q =>
            {
                var allowed = (q.ExpectedCategories ?? [])
                    .Concat(q.AcceptableCategories ?? [])
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                return allowed
                    .SelectMany(code => ArxivCategoryNames.AliasesFor(code)
                        .Where(twin => !allowed.Contains(twin))
                        .Select(twin => $"{q.Id}: allows {code} but not its alias {twin}"))
                    .Order(StringComparer.Ordinal);
            })
            .ToList();

        Assert.Empty(split);
    }

    [Fact]
    public void QueriesWithoutAFrozenPlanAreCategoryEvalTargets()
    {
        // No plan means `eval search` skips the query (and so does the CI gate).
        // That is the deliberate state for a query authored for `eval categories`
        // and not yet compiled+judged; for any other query it is dead weight.
        var orphaned = Set.Queries
            .Where(q => q.Plan is null && q.ExpectedCategories is null)
            .Select(q => q.Id)
            .ToList();

        Assert.Empty(orphaned);
    }
}
