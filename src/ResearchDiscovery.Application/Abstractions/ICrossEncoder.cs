namespace ResearchDiscovery.Application.Abstractions;

/// <summary>
/// Relevance scores from a local cross-encoder: the model reads query and
/// passage TOGETHER (full attention across both), which is far more accurate
/// than comparing two independently-computed vectors — and ~100× slower per
/// pair, which is why it only ever reranks a bounded pool head.
/// </summary>
public interface ICrossEncoder
{
    /// <summary>One relevance logit per passage (higher = more relevant); order matches the input.</summary>
    Task<IReadOnlyList<float>> ScoreAsync(
        string query, IReadOnlyList<string> passages, CancellationToken ct);
}
