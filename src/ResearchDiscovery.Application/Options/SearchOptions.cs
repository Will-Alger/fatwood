using System.ComponentModel.DataAnnotations;

namespace ResearchDiscovery.Application.Options;

/// <summary>
/// Search execution tuning: dense-scan parallelism and candidate-set
/// handling. Defaults are the measured-safe production settings; every knob
/// is a revert switch (parallelism 1 = the original sequential scan,
/// UseCandidateSetCache false = the original per-search SQL materialization).
/// </summary>
public class SearchOptions
{
    public const string SectionName = "Search";

    /// <summary>
    /// Maximum threads for the dense index scan. 0 (default) uses the
    /// processor count; values are clamped to it. 1 restores the sequential
    /// scan.
    /// </summary>
    [Range(0, 64)]
    public int MaxScanParallelism { get; set; }

    /// <summary>
    /// Serve category/no-code candidate sets from the in-memory cache
    /// instead of materializing ids from the database on every search.
    /// </summary>
    public bool UseCandidateSetCache { get; set; } = true;

    /// <summary>
    /// Candidate sets smaller than corpus/divisor are scored by iterating
    /// the candidate ids (binary search into the packed index) instead of
    /// sweeping the whole corpus.
    /// </summary>
    [Range(2, 1024)]
    public int CandidateScanDivisor { get; set; } = 8;

    /// <summary>
    /// Staleness backstop for the candidate-set cache, in minutes; the cache
    /// is invalidated explicitly on ingest/embed, this only bounds a missed
    /// hook. 0 = never expires.
    /// </summary>
    [Range(0, 10080)]
    public int CandidateCacheTtlMinutes { get; set; } = 360;
}
