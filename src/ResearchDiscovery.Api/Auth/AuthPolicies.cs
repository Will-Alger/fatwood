namespace ResearchDiscovery.Api.Auth;

public static class AuthPolicies
{
    /// <summary>Role-gated: account operations, ingestion, settings, bulk analysis.</summary>
    public const string Admin = "Admin";

    /// <summary>Signed in AND activated (past the invite gate) — required to spend tokens.</summary>
    public const string ActiveUser = "ActiveUser";

    /// <summary>Claim stamped by <see cref="UserContextMiddleware"/> when the account is active.</summary>
    public const string ActiveClaim = "app:active";
}
