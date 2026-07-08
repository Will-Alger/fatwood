using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ResearchDiscovery.Application.Abstractions;
using ResearchDiscovery.Infrastructure.Ingestion;
using ResearchDiscovery.Infrastructure.Persistence;
using Xunit;

namespace ResearchDiscovery.IntegrationTests;

/// <summary>
/// Database-level idempotency proof for the upsert: re-running the same page
/// must never create duplicates, and revised entries update in place.
/// </summary>
public sealed class PaperUpserterTests : IDisposable
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly TestDbContextFactory _dbFactory;
    private readonly PaperUpserter _upserter;

    public PaperUpserterTests()
    {
        _connection.Open();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;
        _dbFactory = new TestDbContextFactory(options);
        using var db = _dbFactory.CreateDbContext();
        db.Database.EnsureCreated();
        _upserter = new PaperUpserter(_dbFactory, NullLogger<PaperUpserter>.Instance);
    }

    public void Dispose() => _connection.Dispose();

    [Fact]
    public async Task UpsertPage_RunTwice_AddsOnceThenNoChanges()
    {
        var entries = new[] { Entry("2501.10000"), Entry("2501.10001") };
        var cache = new Dictionary<string, long>(StringComparer.Ordinal);

        var first = await _upserter.UpsertPageAsync(entries, cache, CancellationToken.None);
        var second = await _upserter.UpsertPageAsync(entries, cache, CancellationToken.None);

        Assert.Equal((2, 0), (first.Added, first.Updated));
        Assert.Equal((0, 0), (second.Added, second.Updated));

        await using var db = _dbFactory.CreateDbContext();
        Assert.Equal(2, await db.Papers.CountAsync());
    }

    [Fact]
    public async Task UpsertPage_RevisedEntry_UpdatesInPlace()
    {
        var cache = new Dictionary<string, long>(StringComparer.Ordinal);
        await _upserter.UpsertPageAsync([Entry("2501.20000")], cache, CancellationToken.None);

        var revised = Entry("2501.20000") with
        {
            Version = 2,
            Title = "Revised title",
            Categories = ["cs.LG", "cs.AI"],
        };
        var result = await _upserter.UpsertPageAsync([revised], cache, CancellationToken.None);

        Assert.Equal((0, 1), (result.Added, result.Updated));

        await using var db = _dbFactory.CreateDbContext();
        var paper = await db.Papers.Include(p => p.PaperCategories)
            .SingleAsync(p => p.ArxivId == "2501.20000");
        Assert.Equal(2, paper.LatestVersion);
        Assert.Equal("Revised title", paper.Title);
        Assert.Equal(2, paper.PaperCategories.Count);
    }

    [Fact]
    public async Task UpsertPage_CrossListedPaperSeenFromSecondCategory_IsNotDuplicated()
    {
        var cache = new Dictionary<string, long>(StringComparer.Ordinal);

        // The same paper arrives once via the cs.LG sweep and again via cs.AI.
        var fromCsLg = Entry("2501.30000") with { Categories = ["cs.LG", "cs.AI"] };
        var fromCsAi = fromCsLg;

        await _upserter.UpsertPageAsync([fromCsLg], cache, CancellationToken.None);
        var second = await _upserter.UpsertPageAsync([fromCsAi], cache, CancellationToken.None);

        Assert.Equal(0, second.Added);

        await using var db = _dbFactory.CreateDbContext();
        Assert.Equal(1, await db.Papers.CountAsync());
    }

    private static ArxivEntry Entry(string arxivId) =>
        new(
            arxivId,
            Version: 1,
            Title: $"Paper {arxivId}",
            Abstract: "An abstract.",
            Authors: ["Ada Lovelace"],
            PrimaryCategory: "cs.LG",
            Categories: ["cs.LG"],
            Published: new DateTimeOffset(2025, 1, 15, 12, 0, 0, TimeSpan.Zero),
            Updated: new DateTimeOffset(2025, 1, 15, 12, 0, 0, TimeSpan.Zero),
            AbsUrl: $"https://arxiv.org/abs/{arxivId}v1",
            PdfUrl: $"https://arxiv.org/pdf/{arxivId}v1",
            Doi: null);

    private sealed class TestDbContextFactory(DbContextOptions<AppDbContext> options)
        : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => new(options);
    }
}
