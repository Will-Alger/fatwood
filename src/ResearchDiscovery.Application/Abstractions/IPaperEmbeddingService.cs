namespace ResearchDiscovery.Application.Abstractions;

public sealed record EmbedRunSummary(int Embedded, int Skipped, int Failed);

/// <summary>Embeds papers that don't yet have a current-model vector.</summary>
public interface IPaperEmbeddingService
{
    Task<EmbedRunSummary> EmbedMissingAsync(CancellationToken ct);
}

public sealed record ScoredPaper(long PaperId, float Score);

/// <summary>
/// In-memory cosine index over all paper embeddings. Loaded lazily from the
/// database and invalidated after embedding runs.
/// </summary>
public interface IEmbeddingIndex
{
    /// <summary>Top-N papers by cosine similarity to the query vector, restricted to the candidate set when given.</summary>
    Task<IReadOnlyList<ScoredPaper>> TopAsync(
        float[] query, int n, IReadOnlySet<long>? restrictTo, CancellationToken ct);

    /// <summary>
    /// Top-N papers scored as the average of (a) similarity to the primary
    /// query vector — the whole intent — and (b) the BEST similarity among the
    /// topic vectors. (a) alone averages away single-topic gems; (b) alone
    /// rewards single-topic tunnel vision (measured: nDCG@10 0.38 vs 0.55);
    /// the blend keeps both properties. With no topics this is plain cosine.
    /// </summary>
    Task<IReadOnlyList<ScoredPaper>> TopMultiAsync(
        float[] primary,
        IReadOnlyList<float[]> topics,
        int n,
        IReadOnlySet<long>? restrictTo,
        CancellationToken ct);

    /// <summary>Cosine scores for a specific set of papers against a vector.</summary>
    Task<IReadOnlyDictionary<long, float>> ScoreAsync(
        IEnumerable<long> paperIds, float[] query, CancellationToken ct);

    /// <summary>Drops the cached vectors; the next query reloads from the database.</summary>
    void Invalidate();
}
