namespace ResearchDiscovery.Application.Options;

/// <summary>
/// Outbound email via Azure Communication Services — currently only the
/// Fatwood-branded verification-code emails sent from the OTP event hook.
/// Unset connection string = feature off (Entra falls back to its own email).
/// </summary>
public class EmailOptions
{
    public const string SectionName = "Email";

    public string AcsConnectionString { get; set; } = string.Empty;

    public string From { get; set; } = "noreply@fatwood.io";

    public bool Enabled => !string.IsNullOrWhiteSpace(AcsConnectionString);
}

/// <summary>
/// Validation of Entra's custom-authentication-extension callbacks (the OTP
/// send event). Audience = the auth-events app registration id.
/// </summary>
public class AuthEventsOptions
{
    public const string SectionName = "AuthEvents";

    /// <summary>Entra's fixed caller service principal for custom extensions.</summary>
    public const string MicrosoftCallerAppId = "99045fe1-7639-4a75-9d4a-577b6ca3810f";

    public string Audience { get; set; } = string.Empty;

    public bool Enabled => !string.IsNullOrWhiteSpace(Audience);
}
