namespace ResearchDiscovery.Application.Abstractions;

/// <summary>
/// In-memory BM25 index over paper titles + abstracts. Deliberately NOT
/// database full-text search: an in-process index keeps the provider swap
/// (PostgreSQL ↔ SQL Server) honest and is trivially testable. Exists because
/// embeddings blur exact terminology — "limit order book" as a literal phrase
/// is a stronger signal than a 384-dim vector can express.
/// </summary>
public interface ILexicalIndex
{
    /// <summary>Top-N papers by BM25 against the query text, restricted to the candidate set when given.</summary>
    Task<IReadOnlyList<ScoredPaper>> TopAsync(
        string query, int n, IReadOnlySet<long>? restrictTo, CancellationToken ct,
        DateTimeOffset? publishedAfter = null);

    /// <summary>Drops the cached index; the next query rebuilds from the database.</summary>
    void Invalidate();
}
