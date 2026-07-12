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

    [Theory]
    // The EXACT paths the MSAL custom-auth SDK calls (from its source) — a
    // miss here surfaces to users as "Unexpected end of JSON input".
    [InlineData("oauth2/v2.0/initiate")]
    [InlineData("oauth2/v2.0/challenge")]
    [InlineData("oauth2/v2.0/token")]
    [InlineData("signup/v1.0/start")]
    [InlineData("signup/v1.0/challenge")]
    [InlineData("signup/v1.0/continue")]
    [InlineData("resetpassword/v1.0/start")]
    [InlineData("resetpassword/v1.0/challenge")]
    [InlineData("resetpassword/v1.0/continue")]
    [InlineData("resetpassword/v1.0/submit")]
    [InlineData("resetpassword/v1.0/poll_completion")]
    public async Task Proxy_AllowsEverySdkPath(string path)
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateClient();

        var response = await client.PostAsync(
            $"/auth-proxy/{path}",
            new FormUrlEncodedContent([new KeyValuePair<string, string>("client_id", "x")]));

        // 503 = recognized path, proxy unconfigured in the test host.
        // 404 would mean the allowlist is missing an SDK endpoint.
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
