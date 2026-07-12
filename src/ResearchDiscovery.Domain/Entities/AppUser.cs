namespace ResearchDiscovery.Domain.Entities;

/// <summary>
/// An account holder. Identity (credentials, email verification, password
/// reset) lives in Entra External ID; this row is the app's view of that
/// person — role, budget, preferences — keyed by the directory object id,
/// which is stable per tenant (unlike the per-app pairwise sub claim).
/// </summary>
public class AppUser
{
    public long Id { get; set; }

    /// <summary>Entra External ID object id (oid claim). Unique per tenant.</summary>
    public string ExternalId { get; set; } = string.Empty;

    public string Email { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public UserRole Role { get; set; } = UserRole.Member;

    /// <summary>
    /// False while a signup gate (invite code) is unredeemed. Inactive users
    /// can sign in but cannot spend tokens or write state.
    /// </summary>
    public bool IsActive { get; set; }

    /// <summary>"dark" or "light"; null = client default.</summary>
    public string? ThemePreference { get; set; }

    /// <summary>
    /// The user's own Anthropic API key, encrypted with Data Protection.
    /// Write-only through the API: set, replace, or delete — never returned.
    /// Calls made with it never debit the platform budget.
    /// </summary>
    public string? EncryptedAnthropicKey { get; set; }

    /// <summary>Last 4 characters, the only fragment ever shown back.</summary>
    public string? AnthropicKeyLast4 { get; set; }

    public DateTimeOffset? AnthropicKeySetUtc { get; set; }

    public DateTimeOffset CreatedUtc { get; set; }

    public DateTimeOffset LastSeenUtc { get; set; }
}

public enum UserRole
{
    Member = 0,
    Admin = 1,
}
