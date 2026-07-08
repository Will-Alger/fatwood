using System.Net;
using Xunit;

namespace ResearchDiscovery.IntegrationTests;

public class AdminEndpointTests
{
    [Fact]
    public async Task AdminEndpoints_NoKeyConfigured_Return404()
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateClient();

        var backfill = await client.PostAsync("/api/admin/ingestion/backfill", content: null);
        var runs = await client.GetAsync("/api/admin/ingestion/runs");

        Assert.Equal(HttpStatusCode.NotFound, backfill.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, runs.StatusCode);
    }

    [Fact]
    public async Task AdminEndpoints_WrongKey_Return401()
    {
        using var factory = new ApiFactory { AdminApiKey = "correct-key" };
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Admin-Api-Key", "wrong-key");

        var response = await client.PostAsync("/api/admin/ingestion/delta", content: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task AdminEndpoints_CorrectKey_QueueAndReport()
    {
        using var factory = new ApiFactory { AdminApiKey = "correct-key" };
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Admin-Api-Key", "correct-key");

        var trigger = await client.PostAsync("/api/admin/ingestion/delta", content: null);
        Assert.Equal(HttpStatusCode.Accepted, trigger.StatusCode);

        var runs = await client.GetAsync("/api/admin/ingestion/runs");
        Assert.Equal(HttpStatusCode.OK, runs.StatusCode);
    }

    [Fact]
    public async Task BrowseEndpoints_ExposeNoIngestionSurface()
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateClient();

        // Regular users must have no way to trigger ingestion: the only
        // mapped user-facing routes are the two read-only GETs.
        var postPapers = await client.PostAsync("/api/papers", content: null);
        var postCategories = await client.PostAsync("/api/categories", content: null);

        Assert.Equal(HttpStatusCode.MethodNotAllowed, postPapers.StatusCode);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, postCategories.StatusCode);
    }
}
