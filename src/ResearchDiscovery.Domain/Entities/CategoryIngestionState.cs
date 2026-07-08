namespace ResearchDiscovery.Domain.Entities;

/// <summary>
/// Per-category ingestion progress. The high-water mark is the maximum
/// &lt;published&gt; timestamp seen for the category, so the daily delta job
/// only fetches papers submitted after the last completed run.
/// </summary>
public class CategoryIngestionState
{
    public long CategoryId { get; set; }

    public Category Category { get; set; } = null!;

    /// <summary>Null means the category has never been ingested.</summary>
    public DateTimeOffset? HighWaterMarkUtc { get; set; }

    public DateTimeOffset? LastCompletedRunUtc { get; set; }
}
