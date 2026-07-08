namespace ResearchDiscovery.Domain.Entities;

/// <summary>Join row: a paper belongs to many categories.</summary>
public class PaperCategory
{
    public long PaperId { get; set; }

    public Paper Paper { get; set; } = null!;

    public long CategoryId { get; set; }

    public Category Category { get; set; } = null!;
}
