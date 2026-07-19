using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ResearchDiscovery.Domain.Entities;
using ResearchDiscovery.Infrastructure.Persistence;
using Xunit;

namespace ResearchDiscovery.IntegrationTests;

/// <summary>
/// End-to-end guard for the contractual wildcard slots: a profiled user's
/// search must reserve the final two slots for the least experience-similar
/// pool papers, flag them in the response, and record them in telemetry.
/// Written while investigating the 0%-wildcard-yield report
/// (docs/search-quality.md §5) — this pins the healthy in-repo behavior.
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
    /// Eight cs.LG papers that all match the query "alpha beta gamma", with
    /// rank forced by filler-word count (the stub embedder hashes words, so
    /// extra unique words dilute the cosine). Titles are two characters on
    /// purpose — the stub tokenizer drops them, keeping vectors abstract-only.
    /// </summary>
    private static readonly string[] Fillers =
    [
        "",
        "ember",
        "humid misty",
        "quilt raven sable",
        "tulip umber vexed woven",
        "xylem yeast zonal acorn brine",
        "cedar dusky elfin fjord gusty haiku",
        "ivory jumbo kayak lilac mango nubby oaken",
    ];

    private static async Task SeedRankedCorpusAsync(ApiFactory factory)
    {
        await factory.SeedAsync(db =>
        {
            var csLg = new Category { Code = "cs.LG", Name = "Machine Learning" };
            var now = DateTimeOffset.UtcNow;

            for (var i = 1; i <= Fillers.Length; i++)
            {
                var filler = Fillers[i - 1];
                db.Papers.Add(new Paper
                {
                    ArxivId = $"2501.0000{i}",
                    LatestVersion = 1,
                    Title = $"P{i}",
                    Abstract = string.IsNullOrEmpty(filler)
                        ? "alpha beta gamma"
                        : $"alpha beta gamma {filler}",
                    Authors = "Ada Lovelace",
                    PrimaryCategory = csLg,
                    PublishedUtc = now.AddDays(-i),
                    UpdatedUtc = now.AddDays(-i),
                    AbsUrl = $"https://arxiv.org/abs/2501.0000{i}v1",
                    PdfUrl = $"https://arxiv.org/pdf/2501.0000{i}v1",
                    FirstIngestedUtc = now.AddDays(-i),
                    LastSeenUtc = now.AddDays(-i),
                    PaperCategories = [new PaperCategory { Category = csLg }],
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
                    ModelVersion = factory.ConfiguredEmbeddingModelVersion,
                    Vector = ToBytes(ApiFactory.StubTextEmbedder.Embed(
                        $"{paper.Title}. {paper.Abstract}")),
                    CreatedUtc = DateTimeOffset.UtcNow,
                });
            }
        });
    }

    [Fact]
    public async Task Search_WithProfile_FlagsAndLogsTwoWildcardSlots()
    {
        using var factory = new ApiFactory();
        await SeedRankedCorpusAsync(factory);
        using var client = factory.CreateClient();

        // The profile's experience words are the fillers of P4/P5/P6, making
        // P7 and P8 the least experience-similar papers in the pool tail.
        var profileResponse = await client.PutAsJsonAsync("/api/me/profile", new
        {
            experienceSummary = "quilt tulip xylem",
            goals = "",
            weeklyHours = (int?)null,
        });
        profileResponse.EnsureSuccessStatusCode();

        var response = await client.PostAsJsonAsync("/api/search", new
        {
            plan = new
            {
                interpretation = "test",
                anchorText = "alpha beta gamma",
                categories = Array.Empty<string>(),
                dateWindowDays = (int?)null,
            },
            limit = 5,
        });
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<SearchResultView>();
        Assert.NotNull(result);
        Assert.Equal(5, result.Hits.Count);

        // Relevance order holds for the non-wildcard head...
        Assert.Equal(
            ["2501.00001", "2501.00002", "2501.00003"],
            result.Hits.Take(3).Select(h => h.Paper.ArxivId));
        Assert.All(result.Hits.Take(3), h => Assert.False(h.IsWildcard));

        // ...and the final two slots are the least experience-similar papers
        // (their relative order depends on near-zero cosine ties, so assert
        // the pair, not the ordering).
        Assert.Equal(
            ["2501.00007", "2501.00008"],
            result.Hits.Skip(3).Select(h => h.Paper.ArxivId).Order());
        Assert.All(result.Hits.Skip(3), h => Assert.True(h.IsWildcard));

        // Telemetry must carry the flag — `eval bias` computes wildcard yield
        // from these rows, which is how the 0%-yield regression was noticed.
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var loggedWildcards = await db.SearchEventResults
            .Where(r => r.SearchEventId == result.SearchEventId && r.IsWildcard)
            .OrderBy(r => r.Rank)
            .Select(r => r.Rank)
            .ToListAsync();
        Assert.Equal([4, 5], loggedWildcards);
    }

    [Fact]
    public async Task Search_WithoutProfile_ShowsNoWildcards()
    {
        using var factory = new ApiFactory();
        await SeedRankedCorpusAsync(factory);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/search", new
        {
            plan = new
            {
                interpretation = "test",
                anchorText = "alpha beta gamma",
                categories = Array.Empty<string>(),
                dateWindowDays = (int?)null,
            },
            limit = 5,
        });
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<SearchResultView>();
        Assert.NotNull(result);
        Assert.Equal(5, result.Hits.Count);
        Assert.All(result.Hits, h => Assert.False(h.IsWildcard));
    }

    private sealed record SearchHitView(
        Application.Dtos.PaperDto Paper, float MatchScore, bool IsWildcard,
        string? ExperienceProximity);

    private sealed record SearchResultView(
        long? SearchEventId, List<SearchHitView> Hits, int TotalCandidates);
}
