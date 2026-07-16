using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using ResearchDiscovery.Application.Abstractions;
using Xunit;

namespace ResearchDiscovery.IntegrationTests;

public class AnalysisApiTests
{
    [Fact]
    public async Task AdminAnalysisEndpoints_AsMember_Return403()
    {
        using var factory = new ApiFactory { TestUserExternalId = "member-1" };
        using var client = factory.CreateClient();

        var run = await client.PostAsJsonAsync("/api/admin/analysis/run", new { categoryCode = "cs.LG" });
        var coverage = await client.GetAsync("/api/admin/analysis/coverage");

        Assert.Equal(HttpStatusCode.Forbidden, run.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, coverage.StatusCode);
    }

    [Fact]
    public async Task Selection_AsMember_QueuesAndAnalyzes()
    {
        // The regression this guards: a regular member choosing "Analyze" used
        // to hit the Owner-gated admin controller and get a 403.
        using var factory = new ApiFactory { TestUserExternalId = "analyzing-member" };
        await factory.SeedAsync(TestData.SeedPapersAsync);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/analysis/selection", new { arxivIds = new[] { "2501.00001" } });
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        // The queue worker runs asynchronously on the shared in-memory Sqlite;
        // poll analysis-status until the paper is analyzed for THIS member,
        // tolerating transient "database is locked" contention (the queue job
        // and the poll share one connection).
        var analyzed = false;
        for (var attempt = 0; attempt < 80 && !analyzed; attempt++)
        {
            await Task.Delay(100);
            try
            {
                var status = await client.PostAsJsonAsync(
                    "/api/papers/analysis-status", new { arxivIds = new[] { "2501.00001" } });
                if (!status.IsSuccessStatusCode) continue;
                var body = await status.Content.ReadFromJsonAsync<JsonElement>();
                analyzed = body.GetProperty("analyzed").EnumerateArray().Any();
            }
            catch (Exception ex) when (
                ex is InvalidOperationException or Microsoft.Data.Sqlite.SqliteException)
            {
                // provider contention while the enqueued job holds the connection
            }
        }

        Assert.True(analyzed);
    }

    [Fact]
    public async Task Selection_AnalyzesInSubmittedRankOrder()
    {
        // The UI reveals results as they finish, so they must be analyzed in
        // the caller's submitted (search-rank) order, not the DB's paper-id
        // order. Submit deliberately out of paper-id order and assert the
        // analyzer saw them in submitted order.
        var order = new System.Collections.Concurrent.ConcurrentQueue<string>();
        using var factory = new ApiFactory
        {
            AnalyzePaper = paper =>
            {
                order.Enqueue(paper.ArxivId);
                return ApiFactory.DefaultAnalysis(paper);
            },
        };
        await factory.SeedAsync(TestData.SeedPapersAsync);
        using var client = factory.CreateClient();

        // Papers seed as 2501.00001, 00002, 00003 (ascending id); submit reversed.
        var submitted = new[] { "2501.00003", "2501.00002", "2501.00001" };
        var response = await client.PostAsJsonAsync(
            "/api/analysis/selection", new { arxivIds = submitted });
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        for (var attempt = 0; attempt < 80 && order.Count < 3; attempt++)
        {
            await Task.Delay(100);
        }

        Assert.Equal(submitted, order.ToArray());
    }

    [Fact]
    public async Task Selection_MemberWithNoBudget_Returns402()
    {
        using var factory = new ApiFactory
        {
            TestUserExternalId = "broke-member",
            StarterGrantMicros = 0,
        };
        await factory.SeedAsync(TestData.SeedPapersAsync);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/analysis/selection", new { arxivIds = new[] { "2501.00001" } });

        Assert.Equal(HttpStatusCode.PaymentRequired, response.StatusCode);
    }

    [Fact]
    public async Task Selection_Anonymous_Returns401WhenAuthEnabled()
    {
        using var factory = new AuthOnlyApiFactory();
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/analysis/selection", new { arxivIds = new[] { "2501.00001" } });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>Enables real bearer auth so a tokenless request is 401, not the
    /// dev admin — mirrors AccountApiTests' anonymous posture.</summary>
    private sealed class AuthOnlyApiFactory : ApiFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting("Auth:Authority", "https://example.ciamlogin.com/tenant/v2.0");
            builder.UseSetting("Auth:Audience", "test-audience");
        }
    }

    [Fact]
    public async Task AdminAnalysisRun_UnknownCategory_Returns404()
    {
        using var factory = new ApiFactory();
        await factory.SeedAsync(TestData.SeedPapersAsync);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/admin/analysis/run", new { categoryCode = "nope.XX" });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task AdminAnalysisRun_ValidCategory_QueuesAndCoverageReports()
    {
        using var factory = new ApiFactory();
        await factory.SeedAsync(TestData.SeedPapersAsync);
        using var client = factory.CreateClient();

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

        // Analyze both categories AS the dev user so papers 1 and 3 get scores.
        var userId = await factory.EnsureDevUserAsync();
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<IAnalysisService>();
            await service.AnalyzeAsync(new AnalysisRequest("cs.LG", 10, null), userId, CancellationToken.None);
            await service.AnalyzeAsync(new AnalysisRequest("cs.CR", 10, null), userId, CancellationToken.None);
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

        var userId = await factory.EnsureDevUserAsync();
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<IAnalysisService>();
            await service.AnalyzeAsync(new AnalysisRequest("cs.CR", 10, null), userId, CancellationToken.None);
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
