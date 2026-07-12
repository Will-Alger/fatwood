namespace ResearchDiscovery.Api.Auth;

public static class AuthPolicies
{
    /// <summary>People operations (accounts, grants, invites): Admin or Owner.</summary>
    public const string Admin = "Admin";

    /// <summary>System operations (role changes, LLM settings, ingestion, bulk analysis): Owner only.</summary>
    public const string Owner = "Owner";

    /// <summary>Signed in AND activated (past the invite gate) — required to spend tokens.</summary>
    public const string ActiveUser = "ActiveUser";

    /// <summary>Claim stamped by <see cref="UserContextMiddleware"/> when the account is active.</summary>
    public const string ActiveClaim = "app:active";
}
