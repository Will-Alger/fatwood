using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ResearchDiscovery.Application.Abstractions;
using ResearchDiscovery.Application.Options;
using ResearchDiscovery.Infrastructure.Persistence;
using Xunit;

namespace ResearchDiscovery.IntegrationTests;

/// <summary>
/// Exercises the real AnalysisService orchestration (selection, persistence,
/// idempotency) over Sqlite with the LLM stubbed at the IPaperAnalyzer seam.
/// </summary>
public class AnalysisServiceTests
{
    [Fact]
    public async Task Analyze_WritesResults_AndIsIdempotentOnRerun()
    {
        using var factory = new ApiFactory();
        await factory.SeedAsync(TestData.SeedPapersAsync);

        var first = await RunAsync(factory, new AnalysisRequest("cs.LG", MaxPapers: 10, Since: null));

        Assert.Equal(2, first.PapersSelected);
        Assert.Equal(2, first.PapersAnalyzed);
        Assert.Equal(0, first.PapersDeclined);
        Assert.Equal(0, first.PapersFailed);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var results = await db.AnalysisResults.Include(a => a.Paper).ToListAsync();
            Assert.Equal(2, results.Count);
            Assert.All(results, r =>
            {
                Assert.Equal(50m, r.CompositeScore);
                Assert.Equal("stub-model", r.Model);
                Assert.Equal(AnalysisOptions.CurrentSchemaVersion, r.SchemaVersion);
            });
        }

        // Re-run: already-analyzed papers must not be selected again — no
        // tokens are ever spent twice on the same paper.
        var second = await RunAsync(factory, new AnalysisRequest("cs.LG", MaxPapers: 10, Since: null));
        Assert.Equal(0, second.PapersSelected);
        Assert.Equal(0, second.PapersAnalyzed);
    }

    [Fact]
    public async Task Analyze_RespectsMaxPapers_NewestFirst()
    {
        using var factory = new ApiFactory();
        await factory.SeedAsync(TestData.SeedPapersAsync);

        var summary = await RunAsync(factory, new AnalysisRequest("cs.LG", MaxPapers: 1, Since: null));

        Assert.Equal(1, summary.PapersSelected);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var analyzed = await db.AnalysisResults.Include(a => a.Paper).SingleAsync();

        // The newest cs.LG paper wins the bounded slot.
        Assert.Equal("2501.00001", analyzed.Paper.ArxivId);
    }

    [Fact]
    public async Task Analyze_ModelDecline_IsCountedAndSkipped()
    {
        using var factory = new ApiFactory
        {
            AnalyzePaper = paper =>
                paper.ArxivId == "2501.00003" ? null : ApiFactory.DefaultAnalysis(paper),
        };
        await factory.SeedAsync(TestData.SeedPapersAsync);

        var summary = await RunAsync(factory, new AnalysisRequest("cs.CR", MaxPapers: 10, Since: null));

        Assert.Equal(2, summary.PapersSelected);
        Assert.Equal(1, summary.PapersAnalyzed);
        Assert.Equal(1, summary.PapersDeclined);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(1, await db.AnalysisResults.CountAsync());
    }

    [Fact]
    public async Task Analyze_UnknownCategory_Throws()
    {
        using var factory = new ApiFactory();
        await factory.SeedAsync(TestData.SeedPapersAsync);

        await Assert.ThrowsAsync<UnknownCategoryException>(() =>
            RunAsync(factory, new AnalysisRequest("nope.XX", MaxPapers: 10, Since: null)));
    }

    private static async Task<AnalysisSummary> RunAsync(ApiFactory factory, AnalysisRequest request)
    {
        var userId = await factory.EnsureDevUserAsync();
        await using var scope = factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<IAnalysisService>();
        return await service.AnalyzeAsync(request, userId, CancellationToken.None);
    }
}
