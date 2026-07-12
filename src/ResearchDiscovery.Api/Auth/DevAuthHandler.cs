using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using ResearchDiscovery.Infrastructure.Accounts;

namespace ResearchDiscovery.Api.Auth;

/// <summary>
/// Authentication scheme used when Auth:Authority is not configured (local
/// dev, tests): every request is the synthetic local-dev identity, which the
/// account layer provisions as an active admin. Production startup refuses
/// this scheme unless explicitly overridden — see Program.cs.
/// </summary>
public class DevAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "LocalDev";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var identity = new ClaimsIdentity(
        [
            new Claim("oid", UserAccountService.LocalDevExternalId),
            new Claim("email", "dev@localhost"),
            new Claim("name", "Local Dev"),
        ], SchemeName);

        return Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }
}
