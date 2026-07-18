namespace ResearchDiscovery.Application.Options;

/// <summary>
/// Blob-backed snapshots of the in-memory search indexes. At 300k-paper scale
/// a cold replica would otherwise pay a ~500 MB database scan plus a full
/// corpus re-tokenize on its first search (minutes); downloading prebuilt
/// packed snapshots takes seconds. Unconfigured (no AccountUrl and no
/// ConnectionString) the feature is off and indexes build straight from the
/// database — fine for local/dev corpus sizes.
/// </summary>
public class SearchIndexOptions
{
    public const string SectionName = "SearchIndex";

    /// <summary>Blob-service URL for managed-identity auth in cloud,
    /// e.g. https://acct.blob.core.windows.net.</summary>
    public string? AccountUrl { get; set; }

    /// <summary>Full connection string (local Azurite). Wins over AccountUrl.</summary>
    public string? ConnectionString { get; set; }

    public string ContainerName { get; set; } = "search-index";

    public bool Enabled =>
        !string.IsNullOrWhiteSpace(AccountUrl) || !string.IsNullOrWhiteSpace(ConnectionString);
}
