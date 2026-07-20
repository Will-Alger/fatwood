using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ResearchDiscovery.Application.Abstractions;
using ResearchDiscovery.Domain.Entities;
using ResearchDiscovery.Infrastructure.Persistence;
using Xunit;

namespace ResearchDiscovery.IntegrationTests;

/// <summary>
/// Exercises the deterministic search path end-to-end over Sqlite with the
/// stub embedder: rank ordering, plan filters, and the admin posture of the
/// compile endpoint. No LLM, no network, no model download.
/// </summary>
public class SearchApiTests
{
    private static byte[] ToBytes(float[] vector)
    {
        var bytes = new byte[vector.Length * sizeof(float)];
        Buffer.BlockCopy(vector, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    /// <summary>Seeds papers plus stub-embedder vectors matching the configured model version.</summary>
    private static async Task SeedWithEmbeddingsAsync(ApiFactory factory)
    {
        await factory.SeedAsync(TestData.SeedPapersAsync);
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

    private static object PlanBody(
        string anchorText,
        string[]? categories = null,
        bool? requireNoCode = null) =>
        new
        {
            plan = new
            {
                interpretation = "test",
                anchorText,
                categories = categories ?? [],
                dateWindowDays = (int?)null,
                requireNoCode,
            },
            limit = 10,
        };

    [Fact]
    public async Task Search_RanksTheTopicallyClosestPaperFirst()
    {
        using var factory = new ApiFactory();
        await SeedWithEmbeddingsAsync(factory);
        using var client = factory.CreateClient();

        // The seed set has "Newest ML paper" (cs.LG) and "Security paper" (cs.CR);
        // an anchor made of the ML paper's own words must rank it first.
        var response = await client.PostAsJsonAsync(
            "/api/search", PlanBody("Newest ML paper abstract"));
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<SearchResultView>();
        Assert.NotNull(result);
        Assert.True(result.Hits.Count >= 2);
        Assert.Equal("2501.00001", result.Hits[0].Paper.ArxivId);
        Assert.True(result.Hits[0].MatchScore >= result.Hits[^1].MatchScore);
    }

    [Fact]
    public async Task Search_CategoryFilterRestrictsCandidates()
    {
        using var factory = new ApiFactory();
        await SeedWithEmbeddingsAsync(factory);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/search", PlanBody("paper", categories: ["cs.CR"]));
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<SearchResultView>();
        Assert.NotNull(result);
        Assert.All(result.Hits, h => Assert.Contains("cs.CR", h.Paper.Categories));
        Assert.Equal(2, result.TotalCandidates);
    }

    [Fact]
    public async Task Search_MissingAnchor_Returns400()
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/search", PlanBody(""));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Compile_AsGatedInactiveMember_Returns403()
    {
        // With the invite-code gate on, a fresh member account is inactive
        // and must not be able to spend tokens.
        using var factory = new ApiFactory
        {
            TestUserExternalId = "gated-member",
            RequireInviteCode = true,
        };
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/search/compile", new { query = "x" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Compile_AsMember_UsesStarterBudget()
    {
        // Open signup: a fresh member is active with the $1 starter grant and
        // can compile (the LLM itself is stubbed; only the gate is real).
        using var factory = new ApiFactory { TestUserExternalId = "open-member" };
        await SeedWithEmbeddingsAsync(factory);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/search/compile", new { query = "anomaly detection projects" });
        response.EnsureSuccessStatusCode();

        var me = await client.GetFromJsonAsync<JsonElement>("/api/me");
        Assert.False(me.GetProperty("isActive").ValueKind == JsonValueKind.False);
        Assert.Equal(1_000_000, me.GetProperty("budget").GetProperty("grantedMicros").GetInt64());
        Assert.False(me.GetProperty("budget").GetProperty("unlimited").GetBoolean());
    }

    [Fact]
    public async Task Compile_AsDevAdmin_ReturnsPlanFromCompiler()
    {
        using var factory = new ApiFactory();
        await SeedWithEmbeddingsAsync(factory);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/search/compile", new { query = "anomaly detection projects" });
        response.EnsureSuccessStatusCode();

        var plan = await response.Content.ReadFromJsonAsync<SearchPlan>();
        Assert.NotNull(plan);
        Assert.Contains("anomaly detection projects", plan.Interpretation);
    }

    private sealed record SearchHitView(
        Application.Dtos.PaperDto Paper, float MatchScore, bool IsWildcard, string? ExperienceProximity);

    private sealed record SearchResultView(
        SearchPlan Plan, List<SearchHitView> Hits, int TotalCandidates);
}
