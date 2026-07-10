using System.ComponentModel.DataAnnotations;

namespace ResearchDiscovery.Application.Options;

/// <summary>
/// Ranking signal blend. Defaults are pure embedding similarity — i.e. the
/// original ranker, bit-for-bit. Non-default values are a deliberate human
/// act: `eval tune` searches this space offline and PRINTS the best weights
/// with their measured nDCG delta; nothing ever applies them automatically
/// (auto-tuning from the ranker's own outputs is a feedback loop).
/// </summary>
public class RankingOptions
{
    public const string SectionName = "Ranking";

    /// <summary>Weight on the embedding cosine score.</summary>
    [Range(0.01, 10)]
    public float SimilarityWeight { get; set; } = 1f;

    /// <summary>Weight on the recency signal (exponential half-life decay). 0 disables it.</summary>
    [Range(0, 2)]
    public float RecencyWeight { get; set; }

    [Range(1, 3650)]
    public int RecencyHalfLifeDays { get; set; } = 90;

    /// <summary>Flat bonus for papers with a known code repository. 0 disables it.</summary>
    [Range(0, 2)]
    public float CodeBonus { get; set; }

    public RankingWeights ToWeights() =>
        new(SimilarityWeight, RecencyWeight, RecencyHalfLifeDays, CodeBonus);
}

/// <summary>An immutable weight set, overridable per call for offline tuning.</summary>
public sealed record RankingWeights(
    float SimilarityWeight,
    float RecencyWeight,
    int RecencyHalfLifeDays,
    float CodeBonus)
{
    /// <summary>True when the blend degenerates to plain cosine ordering (the fast path).</summary>
    public bool IsPureSimilarity => RecencyWeight == 0 && CodeBonus == 0;
}
