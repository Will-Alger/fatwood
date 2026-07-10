namespace ResearchDiscovery.Domain.Entities;

/// <summary>
/// External quality signals for a paper, fetched by the `enrich` CLI (never
/// on a request path): citation counts from Semantic Scholar and repository
/// stars from GitHub when the paper advertises code. These are ranking
/// features and analysis inputs — quality priors the corpus text can't fake.
/// </summary>
public class PaperSignal
{
    public long PaperId { get; set; }

    public Paper Paper { get; set; } = null!;

    public int? CitationCount { get; set; }

    public int? InfluentialCitationCount { get; set; }

    public int? GitHubStars { get; set; }

    public DateTimeOffset FetchedUtc { get; set; }
}
