namespace ResearchDiscovery.Application.Abstractions;

/// <summary>
/// Text → L2-normalized embedding vector, computed by a local model. Never
/// calls a paid API: the corpus sift must cost zero tokens by design.
/// </summary>
public interface ITextEmbedder
{
    /// <summary>Dimensionality of vectors produced by this embedder.</summary>
    int Dimensions { get; }

    Task<float[]> EmbedAsync(string text, CancellationToken ct);

    Task<IReadOnlyList<float[]>> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken ct);
}
