using System.Net;
using System.Net.Http.Json;
using ResearchDiscovery.Application.Dtos;
using Xunit;

namespace ResearchDiscovery.IntegrationTests;

public class BookmarkApiTests
{
    [Fact]
    public async Task Bookmark_Toggle_Filter_AndDtoFlag_WorkEndToEnd()
    {
        using var factory = new ApiFactory();
        await factory.SeedAsync(TestData.SeedPapersAsync);
        using var client = factory.CreateClient();

        // Bookmark one paper; idempotent double-PUT.
        Assert.Equal(HttpStatusCode.NoContent,
            (await client.PutAsync("/api/papers/2501.00002/bookmark", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent,
            (await client.PutAsync("/api/papers/2501.00002/bookmark", null)).StatusCode);

        // The DTO flag reflects it.
        var all = await client.GetFromJsonAsync<PagedResult<PaperDto>>("/api/papers");
        Assert.NotNull(all);
        Assert.True(all.Items.Single(p => p.ArxivId == "2501.00002").IsBookmarked);
        Assert.False(all.Items.Single(p => p.ArxivId == "2501.00001").IsBookmarked);

        // The filter returns only bookmarked papers.
        var bookmarked = await client.GetFromJsonAsync<PagedResult<PaperDto>>(
            "/api/papers?bookmarkedOnly=true");
        Assert.NotNull(bookmarked);
        var only = Assert.Single(bookmarked.Items);
        Assert.Equal("2501.00002", only.ArxivId);

        // Unbookmark; the filter empties. DELETE is idempotent too.
        Assert.Equal(HttpStatusCode.NoContent,
            (await client.DeleteAsync("/api/papers/2501.00002/bookmark")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent,
            (await client.DeleteAsync("/api/papers/2501.00002/bookmark")).StatusCode);

        var after = await client.GetFromJsonAsync<PagedResult<PaperDto>>(
            "/api/papers?bookmarkedOnly=true");
        Assert.NotNull(after);
        Assert.Empty(after.Items);
    }

    [Fact]
    public async Task Bookmark_UnknownPaper_Returns404()
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateClient();

        var response = await client.PutAsync("/api/papers/9999.99999/bookmark", null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
