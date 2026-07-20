using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ResearchDiscovery.Domain.Entities;
using ResearchDiscovery.Infrastructure.Persistence;
using Xunit;

namespace ResearchDiscovery.IntegrationTests;

/// <summary>
/// The wildcard exploration guarantee, end-to-end: a signed-in user with an
/// experience profile gets the final result slots filled with the least
/// experience-similar papers from the high-relevance pool, and telemetry
/// records them as wildcards. Written while investigating the 2026-07-19
/// bias report (0 wildcards shown across 35 searches): these tests pin the
/// code path, so if they're green the production regression is data-side —
/// searches ran without a resolved user or with an empty ExperienceSummary,
/// both of which legitimately disable wildcards (asserted below).
/// </summary>
public class WildcardSearchApiTests
{
    private static byte[] ToBytes(float[] vector)
    {
        var bytes = new byte[vector.Length * sizeof(float)];
        Buffer.BlockCopy(vector, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    /// <summary>
    /// Six papers sharing the anchor words "sensor network": two loaded with
    /// the full query phrase (the relevance head), two overlapping the test
    /// profile's firmware/embedded vocabulary (close to home), two about
    /// unrelated hobbies (the expected wildcards).
    /// </summary>
    private static readonly (string ArxivId, string Title)[] Corpus =
    [
        ("2502.00001", "sensor network telemetry pipelines survey"),
        ("2502.00002", "sensor network telemetry pipelines methods extra"),
        ("2502.00003", "sensor network firmware drivers embedded"),
        ("2502.00004", "sensor network gardening botany flowers"),
        ("2502.00005", "sensor network cooking recipes baking"),
        ("2502.00006", "sensor network firmware embedded microcontroller"),
    ];

    private const string ExperienceSummary = "firmware drivers embedded microcontroller development";

    private static async Task SeedCorpusAsync(ApiFactory factory)
    {
        await factory.SeedAsync(db =>
        {
            var category = new Category { Code = "cs.NI", Name = "Networking" };
            var now = DateTimeOffset.UtcNow;
            foreach (var (arxivId, title) in Corpus)
            {
                db.Papers.Add(new Paper
                {
                    ArxivId = arxivId,
                    LatestVersion = 1,
                    Title = title,
                    Abstract = $"Abstract of {title}.",
                    Authors = "Ada Lovelace",
                    PrimaryCategory = category,
                    PublishedUtc = now.AddDays(-1),
                    UpdatedUtc = now.AddDays(-1),
                    AbsUrl = $"https://arxiv.org/abs/{arxivId}v1",
                    PdfUrl = $"https://arxiv.org/pdf/{arxivId}v1",
                    FirstIngestedUtc = now,
                    LastSeenUtc = now,
                    PaperCategories = [new PaperCategory { Category = category }],
                });
            }
            return Task.CompletedTask;
        });
        await factory.SeedAsync(async db =>
        {
            var papers = await db.Papers.ToListAsync();
            foreach (var paper in papers)
            {
                db.PaperEmbeddings.Add(new PaperEmbedding
                {
                    PaperId = paper.Id,
                    ModelVersion = factory.EmbeddingModelVersion,
                    Vector = ToBytes(ApiFactory.StubTextEmbedder.Embed(
                        $"{paper.Title}. {paper.Abstract}")),
                    CreatedUtc = DateTimeOffset.UtcNow,
                });
            }
        });
    }

    private static object PlanBody(int limit) => new
    {
        plan = new
        {
            interpretation = "test",
            anchorText = "sensor network telemetry pipelines",
            categories = Array.Empty<string>(),
            dateWindowDays = (int?)null,
            requireNoCode = (bool?)null,
        },
        limit,
    };

    [Fact]
    public async Task Search_WithExperienceProfile_FillsFinalSlotsWithWildcards()
    {
        using var factory = new ApiFactory();
        await SeedCorpusAsync(factory);
        using var client = factory.CreateClient();

        var profileResponse = await client.PutAsJsonAsync("/api/me/profile", new
        {
            experienceSummary = ExperienceSummary,
            goals = "learn something new",
            weeklyHours = 5,
        });
        profileResponse.EnsureSuccessStatusCode();

        var response = await client.PostAsJsonAsync("/api/search", PlanBody(limit: 4));
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<SearchResultView>();

        Assert.NotNull(result);
        Assert.Equal(4, result.Hits.Count);

        // Head: pure relevance, the two papers carrying the full query phrase.
        Assert.All(result.Hits.Take(2), h => Assert.False(h.IsWildcard));
        Assert.Equal(
            ["2502.00001", "2502.00002"],
            result.Hits.Take(2).Select(h => h.Paper.ArxivId));

        // Tail: the two least experience-similar pool papers (the hobby ones),
        // never the firmware papers the profile is close to.
        var wildcards = result.Hits.Skip(2).ToList();
        Assert.All(wildcards, h => Assert.True(h.IsWildcard));
        Assert.Equal(
            ["2502.00004", "2502.00005"],
            wildcards.Select(h => h.Paper.ArxivId).Order());

        // Telemetry must carry the wildcard flags — eval bias reads these.
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var loggedWildcards = await db.SearchEventResults
            .Where(r => r.SearchEventId == result.SearchEventId && r.IsWildcard)
            .ToListAsync();
        Assert.Equal(2, loggedWildcards.Count);
        Assert.Equal([3, 4], loggedWildcards.Select(r => r.Rank).Order());
    }

    [Fact]
    public async Task Search_WithoutProfile_ShowsNoWildcards()
    {
        // No profile row (or an empty ExperienceSummary) means no experience
        // cluster to escape — wildcards are legitimately off, and every slot
        // logs IsWildcard = false. This is the benign explanation for a
        // 0%-wildcard bias report; anything else is a real regression.
        using var factory = new ApiFactory();
        await SeedCorpusAsync(factory);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/search", PlanBody(limit: 4));
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<SearchResultView>();

        Assert.NotNull(result);
        Assert.Equal(4, result.Hits.Count);
        Assert.All(result.Hits, h => Assert.False(h.IsWildcard));

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var logged = await db.SearchEventResults
            .Where(r => r.SearchEventId == result.SearchEventId)
            .ToListAsync();
        Assert.Equal(4, logged.Count);
        Assert.All(logged, r => Assert.False(r.IsWildcard));
    }

    private sealed record SearchHitView(
        Application.Dtos.PaperDto Paper, float MatchScore, bool IsWildcard,
        string? ExperienceProximity);

    private sealed record SearchResultView(
        long SearchEventId, List<SearchHitView> Hits, int TotalCandidates);
}
