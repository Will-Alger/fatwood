namespace ResearchDiscovery.Application.Options;

/// <summary>
/// JWT bearer validation against the Entra External ID tenant. When Authority
/// is empty (local dev, tests) the API runs with a synthetic local admin user
/// instead of real authentication; production refuses to start that way
/// unless DangerouslyAllowAnonymous is set explicitly.
/// </summary>
public class AuthOptions
{
    public const string SectionName = "Auth";

    /// <summary>OIDC authority, e.g. https://fatwoodio.ciamlogin.com/&lt;tenantId&gt;/v2.0.</summary>
    public string Authority { get; set; } = string.Empty;

    /// <summary>Expected audience: the API app registration's client id.</summary>
    public string Audience { get; set; } = string.Empty;

    /// <summary>Escape hatch for running production-config builds without a tenant.</summary>
    public bool DangerouslyAllowAnonymous { get; set; }

    public bool Enabled => !string.IsNullOrWhiteSpace(Authority);
}
