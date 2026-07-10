using System.ComponentModel.DataAnnotations;

namespace ResearchDiscovery.Application.Options;

/// <summary>
/// One complete ranking configuration: which retrieval stages run and how the
/// final blend weighs its signals. Defaults are the original ranker —
/// single-anchor pure cosine — bit-for-bit. Every flag here ships OFF until
/// the eval harness shows it beats the baseline; flipping one is a deliberate
/// human act recorded in configuration.
/// </summary>
public class RankingProfile
{
    /// <summary>Score papers by their BEST-matching anchor topic instead of the
    /// averaged anchor blob — fixes multi-topic query dilution.</summary>
    public bool UseMultiAnchor { get; set; }

    /// <summary>Fuse dense (embedding) and lexical (BM25) candidate lists with
    /// Reciprocal Rank Fusion — catches exact-terminology matches embeddings blur.</summary>
    public bool UseHybrid { get; set; }

    /// <summary>Re-order the pool head with a local ONNX cross-encoder that reads
    /// query and abstract together.</summary>
    public bool UseReranker { get; set; }

    [Range(0.01, 10)]
    public float SimilarityWeight { get; set; } = 1f;

    [Range(0, 2)]
    public float RecencyWeight { get; set; }

    [Range(1, 3650)]
    public int RecencyHalfLifeDays { get; set; } = 90;

    [Range(0, 2)]
    public float CodeBonus { get; set; }

    /// <summary>Weight on log-scaled citation count (needs `enrich` to have run).</summary>
    [Range(0, 2)]
    public float CitationWeight { get; set; }

    public RankingWeights ToWeights() => new(
        SimilarityWeight, RecencyWeight, RecencyHalfLifeDays, CodeBonus, CitationWeight);
}

public class RankingOptions : RankingProfile
{
    public const string SectionName = "Ranking";

    /// <summary>How deep the cross-encoder reranks when enabled.</summary>
    [Range(10, 500)]
    public int RerankDepth { get; set; } = 100;

    /// <summary>
    /// Team-draft interleaving: product searches mix this profile's results
    /// with <see cref="Candidate"/>'s, tagging each slot's team so real clicks
    /// arbitrate between rankers (`eval bias` reports the score). Eval runs
    /// never interleave — offline metrics measure one ranker at a time.
    /// </summary>
    public bool InterleaveCandidate { get; set; }

    public RankingProfile? Candidate { get; set; }
}

/// <summary>An immutable weight set, overridable per call for offline tuning.</summary>
public sealed record RankingWeights(
    float SimilarityWeight,
    float RecencyWeight,
    int RecencyHalfLifeDays,
    float CodeBonus,
    float CitationWeight)
{
    /// <summary>True when the blend degenerates to plain cosine ordering (the fast path).</summary>
    public bool IsPureSimilarity => RecencyWeight == 0 && CodeBonus == 0 && CitationWeight == 0;
}
