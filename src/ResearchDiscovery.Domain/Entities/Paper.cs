namespace ResearchDiscovery.Domain.Entities;

/// <summary>
/// A single arXiv paper, keyed logically by its versionless arXiv identifier
/// (e.g. "2506.00764"). The upsert key for ingestion is <see cref="ArxivId"/>.
/// </summary>
public class Paper
{
    public long Id { get; set; }

    /// <summary>Versionless arXiv identifier, unique across the corpus.</summary>
    public required string ArxivId { get; set; }

    /// <summary>Latest version number observed in the feed (the "vN" suffix).</summary>
    public int LatestVersion { get; set; }

    public required string Title { get; set; }

    public required string Abstract { get; set; }

    /// <summary>Display string of author names, "; "-delimited.</summary>
    public required string Authors { get; set; }

    public long PrimaryCategoryId { get; set; }

    public Category PrimaryCategory { get; set; } = null!;

    public DateTimeOffset PublishedUtc { get; set; }

    public DateTimeOffset UpdatedUtc { get; set; }

    public required string AbsUrl { get; set; }

    public required string PdfUrl { get; set; }

    public string? Doi { get; set; }

    /// <summary>
    /// Code repository URL extracted from the arXiv comment/abstract, when the
    /// authors advertised one. Null means none was advertised — code may still
    /// exist elsewhere.
    /// </summary>
    public string? CodeUrl { get; set; }

    public DateTimeOffset FirstIngestedUtc { get; set; }

    public DateTimeOffset LastSeenUtc { get; set; }

    public ICollection<PaperCategory> PaperCategories { get; set; } = new List<PaperCategory>();

    public AnalysisResult? AnalysisResult { get; set; }

    public PaperEmbedding? Embedding { get; set; }

    public Bookmark? Bookmark { get; set; }

    public PaperSignal? Signal { get; set; }
}
