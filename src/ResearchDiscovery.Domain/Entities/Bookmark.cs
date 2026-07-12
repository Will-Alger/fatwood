namespace ResearchDiscovery.Domain.Entities;

/// <summary>
/// A paper saved by a user. User state, kept out of the Paper row itself
/// (same separation as AnalysisResult): papers are corpus data, bookmarks are
/// what a user did with them. One row per (user, paper).
/// </summary>
public class Bookmark
{
    public long Id { get; set; }

    /// <summary>
    /// Owning account. Null only for pre-account rows, which the bootstrap
    /// admin claims on first sign-in.
    /// </summary>
    public long? UserId { get; set; }

    public AppUser? User { get; set; }

    public long PaperId { get; set; }

    public Paper Paper { get; set; } = null!;

    public DateTimeOffset CreatedUtc { get; set; }
}
