using System.Net;
using System.Net.Http.Json;
using ResearchDiscovery.Application.Dtos;
using ResearchDiscovery.Domain.Entities;
using Xunit;

namespace ResearchDiscovery.IntegrationTests;

public class BrowseApiTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    private readonly HttpClient _client;

    public BrowseApiTests(ApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
        _factory.SeedAsync(async db =>
        {
            if (!db.Papers.Any())
            {
                await TestData.SeedPapersAsync(db);
            }
        }).GetAwaiter().GetResult();
    }

    [Fact]
    public async Task GetPapers_NoFilter_ReturnsAllSortedNewestFirst()
    {
        var result = await _client.GetFromJsonAsync<PagedResult<PaperDto>>("/api/papers");

        Assert.NotNull(result);
        Assert.Equal(3, result.TotalItems);
        Assert.Equal(
            ["2501.00001", "2501.00003", "2501.00002"],
            result.Items.Select(p => p.ArxivId));
    }

    [Fact]
    public async Task GetPapers_CategoryFilter_ReturnsOnlyMatching()
    {
        var result = await _client.GetFromJsonAsync<PagedResult<PaperDto>>(
            "/api/papers?categories=cs.CR");

        Assert.NotNull(result);
        Assert.Equal(2, result.TotalItems);
        Assert.All(result.Items, p => Assert.Contains("cs.CR", p.Categories));
    }

    [Fact]
    public async Task GetPapers_MultiCategoryFilter_UsesOrSemantics()
    {
        var result = await _client.GetFromJsonAsync<PagedResult<PaperDto>>(
            "/api/papers?categories=cs.LG,cs.CR");

        Assert.NotNull(result);
        Assert.Equal(3, result.TotalItems);
    }

    [Fact]
    public async Task GetPapers_UnknownCategory_ReturnsEmptyNotError()
    {
        var result = await _client.GetFromJsonAsync<PagedResult<PaperDto>>(
            "/api/papers?categories=nope.XX");

        Assert.NotNull(result);
        Assert.Equal(0, result.TotalItems);
    }

    [Fact]
    public async Task GetPapers_AscendingSort_ReversesOrder()
    {
        var result = await _client.GetFromJsonAsync<PagedResult<PaperDto>>(
            "/api/papers?sort=published_asc");

        Assert.NotNull(result);
        Assert.Equal(
            ["2501.00002", "2501.00003", "2501.00001"],
            result.Items.Select(p => p.ArxivId));
    }

    [Fact]
    public async Task GetPapers_Pagination_ReturnsRequestedSlice()
    {
        var result = await _client.GetFromJsonAsync<PagedResult<PaperDto>>(
            "/api/papers?page=2&pageSize=2");

        Assert.NotNull(result);
        Assert.Equal(2, result.Page);
        Assert.Equal(2, result.TotalPages);
        Assert.Single(result.Items);
    }

    [Fact]
    public async Task GetPapers_WindowDays_ExcludesOlderPapers()
    {
        // Seeds publish at -1d, -5d, and -10d; a 7-day window keeps two.
        var result = await _client.GetFromJsonAsync<PagedResult<PaperDto>>(
            "/api/papers?windowDays=7");

        Assert.NotNull(result);
        Assert.Equal(2, result.TotalItems);
        Assert.DoesNotContain("2501.00002", result.Items.Select(p => p.ArxivId));
    }

    [Fact]
    public async Task GetPapers_WindowDaysBelowOne_Returns400()
    {
        var response = await _client.GetAsync("/api/papers?windowDays=0");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task GetPapers_InvalidSort_Returns400()
    {
        var response = await _client.GetAsync("/api/papers?sort=title");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task GetPapers_PageBelowOne_Returns400()
    {
        var response = await _client.GetAsync("/api/papers?page=0");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task GetPapers_AuthorsAreSplitIntoArray()
    {
        var result = await _client.GetFromJsonAsync<PagedResult<PaperDto>>("/api/papers");

        Assert.NotNull(result);
        Assert.Equal(["Ada Lovelace", "Alan Turing"], result.Items[0].Authors);
    }

    [Fact]
    public async Task GetCategories_ReturnsCodesWithCounts()
    {
        var categories = await _client.GetFromJsonAsync<List<CategoryDto>>("/api/categories");

        Assert.NotNull(categories);
        var csLg = Assert.Single(categories, c => c.Code == "cs.LG");
        Assert.Equal(2, csLg.PaperCount);
        var csCr = Assert.Single(categories, c => c.Code == "cs.CR");
        Assert.Equal(2, csCr.PaperCount);
    }

    [Fact]
    public async Task GetCategories_RowNamedByBareCode_ResolvesTaxonomyName()
    {
        // Rows created before their taxonomy entry existed carry Name == Code.
        await _factory.SeedAsync(async db =>
        {
            if (!db.Categories.Any(c => c.Code == "q-bio.NC"))
            {
                db.Categories.Add(new Category { Code = "q-bio.NC", Name = "q-bio.NC" });
                await db.SaveChangesAsync();
            }
        });

        var categories = await _client.GetFromJsonAsync<List<CategoryDto>>("/api/categories");

        Assert.NotNull(categories);
        var stale = Assert.Single(categories, c => c.Code == "q-bio.NC");
        Assert.Equal("Neurons and Cognition", stale.Name);
        var named = Assert.Single(categories, c => c.Code == "cs.LG");
        Assert.Equal("Machine Learning", named.Name);
    }
}
