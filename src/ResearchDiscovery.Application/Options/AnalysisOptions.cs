using System.ComponentModel.DataAnnotations;

namespace ResearchDiscovery.Application.Options;

/// <summary>
/// Phase 2 analysis configuration. The Anthropic API key is deliberately not
/// an option here: the SDK reads ANTHROPIC_API_KEY from the environment, so
/// no secret ever passes through appsettings.
/// </summary>
public class AnalysisOptions
{
    public const string SectionName = "Analysis";

    /// <summary>
    /// Version of the analysis JSON contract produced by the current code.
    /// Bump when the schema changes; stale rows can then be re-analyzed.
    /// </summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>
    /// Analysis runs one LLM call per paper across whole categories, so the
    /// default is deliberately a cheap model, not a frontier one.
    /// </summary>
    [Required]
    public string Model { get; set; } = "claude-haiku-4-5-20251001";

    /// <summary>
    /// Optional server-side fallback model for policy declines; empty
    /// disables the fallback (the default — declined papers are counted and
    /// skipped, which costs nothing). If enabled, pick another inexpensive
    /// model: this is a per-paper batch job.
    /// </summary>
    public string? FallbackModel { get; set; }

    /// <summary>
    /// Optional Anthropic effort level (low | medium | high | xhigh | max).
    /// Only sent when set — not every model supports the parameter; the
    /// default haiku model does not need it.
    /// </summary>
    [RegularExpression("^(low|medium|high|xhigh|max)$")]
    public string? Effort { get; set; }

    /// <summary>Cap applied when a run does not specify MaxPapers explicitly.</summary>
    [Range(1, 500)]
    public int DefaultMaxPapers { get; set; } = 25;

    /// <summary>
    /// Per-request output ceiling. Generous headroom is free (only produced
    /// tokens are billed) and keeps a thinking-enabled model configurable.
    /// </summary>
    [Range(1024, 64000)]
    public int MaxOutputTokens { get; set; } = 16000;
}
