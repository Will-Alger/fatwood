using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ResearchDiscovery.IntegrationTests;

public class TestAuthOptions : AuthenticationSchemeOptions
{
    public string ExternalId { get; set; } = "test-user";

    public string Email { get; set; } = "member@test.local";
}

/// <summary>
/// Authenticates every request as a configurable external identity. Unlike
/// the app's local-dev scheme (always the bootstrap admin), this lets tests
/// run as an ordinary member and assert the 403/invite-gate posture.
/// </summary>
public class TestAuthHandler(
    IOptionsMonitor<TestAuthOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder) : AuthenticationHandler<TestAuthOptions>(options, logger, encoder)
{
    public const string SchemeName = "TestUser";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var identity = new ClaimsIdentity(
        [
            new Claim("oid", Options.ExternalId),
            new Claim("email", Options.Email),
            new Claim("name", "Test Member"),
        ], SchemeName);

        return Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }
}
