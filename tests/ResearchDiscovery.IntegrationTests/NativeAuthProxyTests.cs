using System.Net;
using Xunit;

namespace ResearchDiscovery.IntegrationTests;

/// <summary>
/// The native-auth proxy's guard rails: only documented native-auth paths
/// forward, and an unconfigured deployment (no Auth:Authority — the test
/// host) answers 503 instead of forwarding anywhere.
/// </summary>
public class NativeAuthProxyTests
{
    [Fact]
    public async Task Proxy_WithoutAuthority_Returns503()
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateClient();

        var response = await client.PostAsync(
            "/auth-proxy/oauth2/v2.0/initiate",
            new FormUrlEncodedContent([new KeyValuePair<string, string>("client_id", "x")]));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task Proxy_UnknownPath_Returns404()
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateClient();

        var response = await client.PostAsync(
            "/auth-proxy/oauth2/v2.0/authorize", // browser flow — NOT proxied
            new FormUrlEncodedContent([]));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

}
