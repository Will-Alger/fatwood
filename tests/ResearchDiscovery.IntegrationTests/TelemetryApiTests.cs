using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ResearchDiscovery.Domain.Entities;
using ResearchDiscovery.Infrastructure.Persistence;
using Xunit;

namespace ResearchDiscovery.IntegrationTests;

/// <summary>
/// The offline-quality feedback loop's data layer: searches log events with
/// per-rank results, and bookmark/analyze actions join back to (search, rank).
/// </summary>
public class TelemetryApiTests
{
    private static byte[] ToBytes(float[] vector)
    {
        var bytes = new byte[vector.Length * sizeof(float)];
        Buffer.BlockCopy(vector, 0, bytes, 0, bytes.Length);
        return bytes;
    }

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
                    ModelVersion = "all-MiniLM-L6-v2",
                    Vector = ToBytes(ApiFactory.StubTextEmbedder.Embed(
                        $"{paper.Title}. {paper.Abstract}")),
                    CreatedUtc = DateTimeOffset.UtcNow,
                });
            }
        });
    }

    private static object PlanBody(string anchorText, string? queryText = null) => new
    {
        plan = new
        {
            interpretation = "test",
            anchorText,
            categories = Array.Empty<string>(),
            dateWindowDays = (int?)null,
            requireNoCode = (bool?)null,
        },
        limit = 10,
        queryText,
    };

    [Fact]
    public async Task Search_LogsEventWithRankedResults_AndReturnsItsId()
    {
        using var factory = new ApiFactory();
        await SeedWithEmbeddingsAsync(factory);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/search", PlanBody("Newest ML paper abstract", "find me ml papers"));
        response.EnsureSuccessStatusCode();

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var searchEventId = body.RootElement.GetProperty("searchEventId").GetInt64();
        Assert.True(searchEventId > 0);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var logged = await db.SearchEvents
            .Include(e => e.Results)
            .SingleAsync(e => e.Id == searchEventId);

        Assert.Equal("find me ml papers", logged.QueryText);
        Assert.Contains("Newest ML paper abstract", logged.PlanJson);
        Assert.True(logged.Results.Count >= 2);
        Assert.Equal(
            Enumerable.Range(1, logged.Results.Count),
            logged.Results.OrderBy(r => r.Rank).Select(r => r.Rank));
    }

    [Fact]
    public async Task Bookmark_WithSearchContext_LogsInteractionWithResolvedRank()
    {
        using var factory = new ApiFactory();
        await SeedWithEmbeddingsAsync(factory);
        using var client = factory.CreateClient();

        var search = await client.PostAsJsonAsync(
            "/api/search", PlanBody("Newest ML paper abstract"));
        search.EnsureSuccessStatusCode();
        using var body = JsonDocument.Parse(await search.Content.ReadAsStringAsync());
        var searchEventId = body.RootElement.GetProperty("searchEventId").GetInt64();
        var topArxivId = body.RootElement.GetProperty("hits")[0]
            .GetProperty("paper").GetProperty("arxivId").GetString();

        // Rank deliberately omitted: the server resolves it from the log.
        var put = await client.PutAsync(
            $"/api/papers/{topArxivId}/bookmark?searchEventId={searchEventId}", null);
        put.EnsureSuccessStatusCode();

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var interaction = await db.InteractionEvents.SingleAsync();
        Assert.Equal(InteractionType.Bookmarked, interaction.Type);
        Assert.Equal(searchEventId, interaction.SearchEventId);
        Assert.Equal(1, interaction.Rank);
    }

    [Fact]
    public async Task Bookmark_WithoutContext_StillLogsInteraction()
    {
        using var factory = new ApiFactory();
        await factory.SeedAsync(TestData.SeedPapersAsync);
        using var client = factory.CreateClient();

        (await client.PutAsync("/api/papers/2501.00001/bookmark", null)).EnsureSuccessStatusCode();
        (await client.DeleteAsync("/api/papers/2501.00001/bookmark", default)).EnsureSuccessStatusCode();

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var interactions = await db.InteractionEvents.OrderBy(i => i.Id).ToListAsync();
        Assert.Equal(
            new[] { InteractionType.Bookmarked, InteractionType.Unbookmarked },
            interactions.Select(i => i.Type));
        Assert.All(interactions, i => Assert.Null(i.SearchEventId));
    }

    [Fact]
    public async Task AnalyzeSelection_WithSearchContext_LogsAnalyzedInteractions()
    {
        using var factory = new ApiFactory { AdminApiKey = "k" };
        await SeedWithEmbeddingsAsync(factory);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Admin-Api-Key", "k");

        var search = await client.PostAsJsonAsync(
            "/api/search", PlanBody("Newest ML paper abstract"));
        using var body = JsonDocument.Parse(await search.Content.ReadAsStringAsync());
        var searchEventId = body.RootElement.GetProperty("searchEventId").GetInt64();
        var topArxivId = body.RootElement.GetProperty("hits")[0]
            .GetProperty("paper").GetProperty("arxivId").GetString();

        var response = await client.PostAsJsonAsync("/api/admin/analysis/selection",
            new { arxivIds = new[] { topArxivId }, searchEventId });
        response.EnsureSuccessStatusCode();

        // The interaction row is written before the 202 returns, but the
        // enqueued analysis job runs concurrently on the shared in-memory
        // Sqlite — retry briefly so provider-level contention can't flake us.
        InteractionEvent? interaction = null;
        for (var attempt = 0; attempt < 10 && interaction is null; attempt++)
        {
            try
            {
                await using var scope = factory.Services.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                interaction = await db.InteractionEvents.SingleOrDefaultAsync(
                    i => i.Type == InteractionType.AnalyzedFromSearch);
            }
            catch (InvalidOperationException) when (attempt < 9)
            {
                // transient provider contention — retry
            }

            if (interaction is null)
            {
                await Task.Delay(100);
            }
        }

        Assert.NotNull(interaction);
        Assert.Equal(searchEventId, interaction.SearchEventId);
        Assert.Equal(1, interaction.Rank);
    }
}
