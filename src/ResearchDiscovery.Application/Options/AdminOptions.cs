namespace ResearchDiscovery.Application.Options;

public sealed class AdminOptions
{
    public const string SectionName = "Admin";

    /// <summary>
    /// API key protecting the admin ingestion endpoints. When empty the admin
    /// endpoints are disabled entirely (they return 404), so a deployment
    /// without the secret has no admin surface at all.
    /// </summary>
    public string ApiKey { get; set; } = string.Empty;

    public bool Enabled => !string.IsNullOrWhiteSpace(ApiKey);
}
