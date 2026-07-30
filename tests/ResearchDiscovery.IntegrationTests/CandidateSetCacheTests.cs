using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ResearchDiscovery.Application.Abstractions;
using ResearchDiscovery.Domain.Entities;
using ResearchDiscovery.Infrastructure.Persistence;
using Xunit;

namespace ResearchDiscovery.IntegrationTests;

/// <summary>
/// The candidate-set cache must return exactly the ids the SQL filter path
/// returns — ANY-of categories, CodeUrl null check, and the sub-day date
/// cutoff — and must reload after Invalidate().
/// </summary>
public class CandidateSetCacheTests
{
    private static async Task SeedAsync(ApiFactory factory) =>
        await factory.SeedAsync(async db =>
        {
            await TestData.SeedPapersAsync(db);
            await db.SaveChangesAsync();

            // Give the security paper an advertised repo so RequireNoCode
            // actually excludes something.
            var security = await db.Papers.SingleAsync(p => p.ArxivId == "2501.00003");
            security.CodeUrl = "https://github.com/example/security";
        });

    private static async Task<HashSet<long>> SqlCandidatesAsync(
        AppDbContext db, string[] categories, bool requireNoCode, DateTimeOffset? publishedAfter)
    {
        var query = db.Papers.AsNoTracking().AsQueryable();
        if (categories.Length > 0)
        {
            query = query.Where(
                p => p.PaperCategories.Any(pc => categories.Contains(pc.Category.Code)));
        }

        if (requireNoCode)
        {
            query = query.Where(p => p.CodeUrl == null);
        }

        if (publishedAfter is { } cutoff)
        {
            query = query.Where(p => p.PublishedUtc >= cutoff);
        }

        return (await query.Select(p => p.Id).ToListAsync()).ToHashSet();
    }

    private static async Task AssertParityAsync(
        ApiFactory factory, string[] categories, bool requireNoCode, DateTimeOffset? publishedAfter)
    {
        var cache = factory.Services.GetRequiredService<ICandidateSetCache>();
        var cached = await cache.GetAsync(
            categories, requireNoCode, publishedAfter, CancellationToken.None);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var expected = await SqlCandidatesAsync(db, categories, requireNoCode, publishedAfter);

        Assert.True(expected.SetEquals(cached),
            $"cache [{string.Join(",", cached)}] != sql [{string.Join(",", expected)}] " +
            $"for cats=[{string.Join(",", categories)}] noCode={requireNoCode} after={publishedAfter}");
        Assert.NotEmpty(expected); // a vacuous parity check pins nothing
    }

    [Fact]
    public async Task MatchesSqlFilter_AcrossCombinations()
    {
        using var factory = new ApiFactory();
        await SeedAsync(factory);

        await AssertParityAsync(factory, ["cs.LG"], requireNoCode: false, publishedAfter: null);
        await AssertParityAsync(factory, ["cs.LG", "cs.CR"], requireNoCode: false, publishedAfter: null);
        await AssertParityAsync(factory, ["cs.CR"], requireNoCode: true, publishedAfter: null);
        await AssertParityAsync(factory, [], requireNoCode: true, publishedAfter: null);
        await AssertParityAsync(
            factory, ["cs.LG"], requireNoCode: false,
            publishedAfter: DateTimeOffset.UtcNow.AddDays(-7));
    }

    [Fact]
    public async Task DateCutoff_IsSubDayExact()
    {
        using var factory = new ApiFactory();
        await SeedAsync(factory);

        // The newest ML paper was published exactly UtcNow-1d (to the tick).
        DateTimeOffset published;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            published = (await db.Papers.SingleAsync(p => p.ArxivId == "2501.00001")).PublishedUtc;
        }

        var cache = factory.Services.GetRequiredService<ICandidateSetCache>();
        var justBefore = await cache.GetAsync(
            ["cs.LG"], false, published.AddTicks(-1), CancellationToken.None);
        var exactlyAt = await cache.GetAsync(
            ["cs.LG"], false, published, CancellationToken.None);
        var justAfter = await cache.GetAsync(
            ["cs.LG"], false, published.AddTicks(1), CancellationToken.None);

        long newestId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            newestId = (await db.Papers.SingleAsync(p => p.ArxivId == "2501.00001")).Id;
        }

        Assert.Contains(newestId, justBefore);
        Assert.Contains(newestId, exactlyAt);   // SQL semantics: >= is inclusive
        Assert.DoesNotContain(newestId, justAfter);
    }

    [Fact]
    public async Task Invalidate_PicksUpNewPapers()
    {
        using var factory = new ApiFactory();
        await SeedAsync(factory);

        var cache = factory.Services.GetRequiredService<ICandidateSetCache>();
        var before = await cache.GetAsync(["cs.LG"], false, null, CancellationToken.None);

        await factory.SeedAsync(async db =>
        {
            var csLg = await db.Categories.SingleAsync(c => c.Code == "cs.LG");
            db.Papers.Add(new Paper
            {
                ArxivId = "2501.00099",
                LatestVersion = 1,
                Title = "Fresh paper",
                Abstract = "Abstract of Fresh paper.",
                Authors = "Grace Hopper",
                PrimaryCategory = csLg,
                PublishedUtc = DateTimeOffset.UtcNow,
                UpdatedUtc = DateTimeOffset.UtcNow,
                AbsUrl = "https://arxiv.org/abs/2501.00099v1",
                PdfUrl = "https://arxiv.org/pdf/2501.00099v1",
                FirstIngestedUtc = DateTimeOffset.UtcNow,
                LastSeenUtc = DateTimeOffset.UtcNow,
                PaperCategories = [new PaperCategory { Category = csLg }],
            });
        });

        // Stale snapshot until told otherwise…
        var stale = await cache.GetAsync(["cs.LG"], false, null, CancellationToken.None);
        Assert.Equal(before.Count, stale.Count);

        // …then a reload sees the new paper.
        cache.Invalidate();
        var fresh = await cache.GetAsync(["cs.LG"], false, null, CancellationToken.None);
        Assert.Equal(before.Count + 1, fresh.Count);
    }
}
