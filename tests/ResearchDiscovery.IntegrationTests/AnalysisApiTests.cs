using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ResearchDiscovery.Application.Abstractions;
using Xunit;

namespace ResearchDiscovery.IntegrationTests;

public class AnalysisApiTests
{
    [Fact]
    public async Task AdminAnalysisEndpoints_NoKeyConfigured_Return404()
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateClient();

        var run = await client.PostAsJsonAsync("/api/admin/analysis/run", new { categoryCode = "cs.LG" });
        var coverage = await client.GetAsync("/api/admin/analysis/coverage");

        Assert.Equal(HttpStatusCode.NotFound, run.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, coverage.StatusCode);
    }

    [Fact]
    public async Task AdminAnalysisRun_WrongKey_Returns401()
    {
        using var factory = new ApiFactory { AdminApiKey = "correct-key" };
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Admin-Api-Key", "wrong-key");

        var response = await client.PostAsJsonAsync(
            "/api/admin/analysis/run", new { categoryCode = "cs.LG" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task AdminAnalysisRun_UnknownCategory_Returns404()
    {
        using var factory = new ApiFactory { AdminApiKey = "correct-key" };
        await factory.SeedAsync(TestData.SeedPapersAsync);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Admin-Api-Key", "correct-key");

        var response = await client.PostAsJsonAsync(
            "/api/admin/analysis/run", new { categoryCode = "nope.XX" });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task AdminAnalysisRun_ValidCategory_QueuesAndCoverageReports()
    {
        using var factory = new ApiFactory { AdminApiKey = "correct-key" };
        await factory.SeedAsync(TestData.SeedPapersAsync);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Admin-Api-Key", "correct-key");

        var run = await client.PostAsJsonAsync(
            "/api/admin/analysis/run", new { categoryCode = "cs.LG", maxPapers = 5 });
        Assert.Equal(HttpStatusCode.Accepted, run.StatusCode);

        // The queue worker runs asynchronously; poll coverage briefly until
        // the two cs.LG papers are analyzed.
        var analyzed = 0;
        for (var attempt = 0; attempt < 50 && analyzed < 2; attempt++)
        {
            await Task.Delay(100);
            var coverage = await client.GetFromJsonAsync<JsonElement>("/api/admin/analysis/coverage");
            analyzed = coverage.EnumerateArray()
                .Single(c => c.GetProperty("categoryCode").GetString() == "cs.LG")
                .GetProperty("analyzedPapers").GetInt32();
        }

        Assert.Equal(2, analyzed);
    }

    [Fact]
    public async Task Browse_ScoreSort_OrdersAnalyzedPapersFirstByScore()
    {
        using var factory = new ApiFactory
        {
            // Distinct scores per paper; the oldest paper stays unanalyzed.
            AnalyzePaper = paper => paper.ArxivId switch
            {
                "2501.00001" => ApiFactory.DefaultAnalysis(paper)! with { CompositeScore = 42m },
                "2501.00003" => ApiFactory.DefaultAnalysis(paper)! with { CompositeScore = 88m },
                _ => null,
            },
        };
        await factory.SeedAsync(TestData.SeedPapersAsync);

        // Analyze both categories so papers 1 and 3 get scores.
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<IAnalysisService>();
            await service.AnalyzeAsync(new AnalysisRequest("cs.LG", 10, null), CancellationToken.None);
            await service.AnalyzeAsync(new AnalysisRequest("cs.CR", 10, null), CancellationToken.None);
        }

        using var client = factory.CreateClient();
        var page = await client.GetFromJsonAsync<JsonElement>("/api/papers?sort=score_desc");
        var ids = page.GetProperty("items").EnumerateArray()
            .Select(p => p.GetProperty("arxivId").GetString())
            .ToList();

        // 88 first, then 42, then the unanalyzed paper last.
        Assert.Equal(["2501.00003", "2501.00001", "2501.00002"], ids);

        var top = page.GetProperty("items")[0].GetProperty("analysis");
        Assert.Equal(88m, top.GetProperty("compositeScore").GetDecimal());
        Assert.Equal("Stub analysis.",
            top.GetProperty("details").GetProperty("summary").GetString());
    }

    [Fact]
    public async Task Browse_AnalyzedOnly_FiltersOutUnanalyzedPapers()
    {
        using var factory = new ApiFactory();
        await factory.SeedAsync(TestData.SeedPapersAsync);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<IAnalysisService>();
            await service.AnalyzeAsync(new AnalysisRequest("cs.CR", 10, null), CancellationToken.None);
        }

        using var client = factory.CreateClient();
        var page = await client.GetFromJsonAsync<JsonElement>("/api/papers?analyzedOnly=true");

        Assert.Equal(2, page.GetProperty("totalItems").GetInt32());
        Assert.All(page.GetProperty("items").EnumerateArray(), p =>
            Assert.NotEqual(JsonValueKind.Null, p.GetProperty("analysis").ValueKind));
    }

    [Fact]
    public async Task Browse_InvalidSort_Returns400()
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/papers?sort=score_asc");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
