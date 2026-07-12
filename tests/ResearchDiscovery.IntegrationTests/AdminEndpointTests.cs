using System.Net;
using Xunit;

namespace ResearchDiscovery.IntegrationTests;

public class AdminEndpointTests
{
    [Fact]
    public async Task AdminEndpoints_AsLocalDevAdmin_QueueAndReport()
    {
        // With no tenant configured the host authenticates everything as the
        // local-dev identity, which the account layer provisions as an admin.
        using var factory = new ApiFactory();
        using var client = factory.CreateClient();

        var trigger = await client.PostAsync("/api/admin/ingestion/delta", content: null);
        Assert.Equal(HttpStatusCode.Accepted, trigger.StatusCode);

        var runs = await client.GetAsync("/api/admin/ingestion/runs");
        Assert.Equal(HttpStatusCode.OK, runs.StatusCode);
    }

    [Fact]
    public async Task AdminEndpoints_AsMember_Return403()
    {
        using var factory = new ApiFactory { TestUserExternalId = "member-1" };
        using var client = factory.CreateClient();

        var trigger = await client.PostAsync("/api/admin/ingestion/delta", content: null);
        var runs = await client.GetAsync("/api/admin/ingestion/runs");

        Assert.Equal(HttpStatusCode.Forbidden, trigger.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, runs.StatusCode);
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
