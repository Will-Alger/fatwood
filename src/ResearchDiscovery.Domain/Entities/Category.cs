namespace ResearchDiscovery.Domain.Entities;

/// <summary>
/// An arXiv taxonomy category (e.g. "cs.LG"). Rows are created during ingestion
/// from category terms observed in the live feed — never seeded.
/// </summary>
public class Category
{
    public long Id { get; set; }

    /// <summary>arXiv category code, e.g. "cs.LG". Unique.</summary>
    public required string Code { get; set; }

    /// <summary>Human-readable name; defaults to the code when no mapping is known.</summary>
    public required string Name { get; set; }

    public ICollection<PaperCategory> PaperCategories { get; set; } = new List<PaperCategory>();
}
