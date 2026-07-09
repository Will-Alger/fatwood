namespace ResearchDiscovery.Domain.Entities;

/// <summary>
/// A saved paper. User state, kept out of the Paper row itself (same
/// separation as AnalysisResult): papers are corpus data, bookmarks are
/// what the user did with them.
/// </summary>
public class Bookmark
{
    public long PaperId { get; set; }

    public Paper Paper { get; set; } = null!;

    public DateTimeOffset CreatedUtc { get; set; }
}
