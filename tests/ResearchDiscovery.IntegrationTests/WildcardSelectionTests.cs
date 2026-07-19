using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ResearchDiscovery.Application.Abstractions;
using ResearchDiscovery.Domain.Entities;
using ResearchDiscovery.Infrastructure.Persistence;
using Xunit;

namespace ResearchDiscovery.IntegrationTests;

/// <summary>
/// End-to-end coverage of the wildcard exploration guarantee: a profile-owning
/// user searching a pool larger than the result limit must receive exactly two
/// wildcard slots (the least experience-similar papers from the high-relevance
/// pool), and telemetry must record them as wildcards. Motivated by the
/// 2026-07-19 bias report showing zero wildcards across all logged searches —
/// this pins the contract the product path is supposed to uphold.
/// </summary>
public class WildcardSelectionTests
{
    // Two topic clusters far apart under the word-hash stub embedder: the
    // profile lives in the ML cluster, so wildcards must come from databases.
    private static readonly string[] MlPhrases =
    [
        "transformer attention language models pretraining",
        "neural network gradient descent optimization",
        "transformer language models fine tuning",
        "attention neural network embeddings",
        "language models tokenization pretraining corpus",
        "gradient descent neural optimization schedules",
        "transformer attention heads interpretability",
        "neural embeddings contrastive pretraining",
    ];

    private static readonly string[] DbPhrases =
    [
        "btree storage engine compaction",
        "columnar storage vectorized execution",
        "write ahead logging checkpoint recovery",
        "query planner join reordering statistics",
        "lsm tree compaction amplification",
        "buffer pool eviction page replacement",
        "distributed consensus replication log",
        "secondary indexing covering scans",
    ];

    private static byte[] ToBytes(float[] vector)
    {
        var bytes = new byte[vector.Length * sizeof(float)];
        Buffer.BlockCopy(vector, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    /// <summary>Seeds one category with both clusters, embeddings included.</summary>
    private static async Task SeedTwoClustersAsync(ApiFactory factory)
    {
        await factory.SeedAsync(db =>
        {
            var csLg = new Category { Code = "cs.LG", Name = "Machine Learning" };
            var now = DateTimeOffset.UtcNow;
            var n = 0;

            foreach (var (phrase, group) in
                     MlPhrases.Select(p => (p, "ml")).Concat(DbPhrases.Select(p => (p, "db"))))
            {
                n++;
                db.Papers.Add(new Paper
                {
                    ArxivId = $"2502.1{n:D4}",
                    LatestVersion = 1,
                    Title = $"{group} paper {n}: {phrase}",
                    Abstract = $"We study {phrase} in depth. More about {phrase}.",
                    Authors = "Ada Lovelace",
                    PrimaryCategory = csLg,
                    PublishedUtc = now.AddDays(-n),
                    UpdatedUtc = now.AddDays(-n),
                    AbsUrl = $"https://arxiv.org/abs/2502.1{n:D4}v1",
                    PdfUrl = $"https://arxiv.org/pdf/2502.1{n:D4}v1",
                    FirstIngestedUtc = now,
                    LastSeenUtc = now,
                    PaperCategories = [new PaperCategory { Category = csLg }],
                });
            }

            return Task.CompletedTask;
        });

        // Vectors must carry the CONFIGURED model version: rows tagged with
        // any other version are invisible to the index, and search silently
        // degrades to lexical-only (which is exactly the failure mode the
        // wildcard tests exist to catch).
        var modelVersion = factory.ConfiguredModelVersion;
        await factory.SeedAsync(async db =>
        {
            var papers = await db.Papers.ToListAsync();
            foreach (var paper in papers)
            {
                db.PaperEmbeddings.Add(new PaperEmbedding
                {
                    PaperId = paper.Id,
                    ModelVersion = modelVersion,
                    Vector = ToBytes(ApiFactory.StubTextEmbedder.Embed(
                        $"{paper.Title}. {paper.Abstract}")),
                    CreatedUtc = DateTimeOffset.UtcNow,
                });
            }
        });
    }

    private static object SearchBody(int limit) => new
    {
        plan = new
        {
            interpretation = "test",
            // Anchor sits squarely in the ML cluster, so the top of the pool
            // is ML and the db cluster only enters through wildcard slots.
            anchorText = "transformer attention neural network language models pretraining",
            categories = Array.Empty<string>(),
            dateWindowDays = (int?)null,
        },
        limit,
        queryText = "wildcard e2e",
    };

    [Fact]
    public async Task Search_WithProfileAndDeepPool_FillsBothWildcardSlots()
    {
        using var factory = new ApiFactory();
        await SeedTwoClustersAsync(factory);
        using var client = factory.CreateClient();

        var profileResponse = await client.PutAsJsonAsync("/api/me/profile", new
        {
            experienceSummary = "transformer attention language models neural network pretraining",
            goals = "explore",
            weeklyHours = 5,
        });
        profileResponse.EnsureSuccessStatusCode();

        var response = await client.PostAsJsonAsync("/api/search", SearchBody(limit: 10));
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<SearchResultView>();

        Assert.NotNull(result);
        Assert.True(result.Hits.Count == 10,
            $"cand={result.TotalCandidates} hits={result.Hits.Count}: " +
            string.Join(" | ", result.Hits.Select(h => h.Paper.Title)));

        // The contract: exactly WildcardSlots wildcards, in the final slots.
        var wildcards = result.Hits.Where(h => h.IsWildcard).ToList();
        Assert.Equal(2, wildcards.Count);
        Assert.True(result.Hits[^1].IsWildcard);
        Assert.True(result.Hits[^2].IsWildcard);

        // Wildcards escape the comfort zone: with the profile parked in the
        // ML cluster, both must come from the database cluster.
        Assert.All(wildcards, w => Assert.StartsWith("db paper", w.Paper.Title));

        // Telemetry must see the same thing the user does — the bias report's
        // wildcard-yield section reads these rows.
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var logged = await db.SearchEventResults.CountAsync(r => r.IsWildcard);
        Assert.Equal(2, logged);
    }

    [Fact]
    public async Task Search_WithoutProfile_HasNoWildcards()
    {
        using var factory = new ApiFactory();
        await SeedTwoClustersAsync(factory);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/search", SearchBody(limit: 10));
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<SearchResultView>();

        Assert.NotNull(result);
        Assert.Equal(10, result.Hits.Count);
        Assert.All(result.Hits, h => Assert.False(h.IsWildcard));
    }

    [Fact]
    public async Task Search_PoolNoDeeperThanLimit_HasNoWildcards()
    {
        using var factory = new ApiFactory();
        await SeedTwoClustersAsync(factory);
        using var client = factory.CreateClient();

        var profileResponse = await client.PutAsJsonAsync("/api/me/profile", new
        {
            experienceSummary = "transformer attention language models",
            goals = "explore",
            weeklyHours = 5,
        });
        profileResponse.EnsureSuccessStatusCode();

        // 16 seeded papers, limit 20: the whole pool fits, nothing to trade
        // for exploration.
        var response = await client.PostAsJsonAsync("/api/search", SearchBody(limit: 20));
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<SearchResultView>();

        Assert.NotNull(result);
        Assert.Equal(16, result.Hits.Count);
        Assert.All(result.Hits, h => Assert.False(h.IsWildcard));
    }

    private sealed record SearchHitView(
        Application.Dtos.PaperDto Paper, float MatchScore, bool IsWildcard, string? ExperienceProximity);

    private sealed record SearchResultView(
        SearchPlan Plan, List<SearchHitView> Hits, int TotalCandidates);
}
