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

    [Required]
    public string Model { get; set; } = "claude-fable-5";

    /// <summary>
    /// Server-side fallback model. claude-fable-5 runs safety classifiers that
    /// can decline benign security papers (cs.CR is a target category), so a
    /// fallback keeps those analyses from silently failing.
    /// </summary>
    [Required]
    public string FallbackModel { get; set; } = "claude-opus-4-8";

    /// <summary>Anthropic effort level: low | medium | high | xhigh | max.</summary>
    [RegularExpression("^(low|medium|high|xhigh|max)$")]
    public string Effort { get; set; } = "medium";

    /// <summary>Cap applied when a run does not specify MaxPapers explicitly.</summary>
    [Range(1, 500)]
    public int DefaultMaxPapers { get; set; } = 25;

    /// <summary>
    /// Per-request output ceiling. Thinking tokens count against this on
    /// claude-fable-5, so it needs headroom beyond the JSON itself.
    /// </summary>
    [Range(1024, 64000)]
    public int MaxOutputTokens { get; set; } = 16000;
}
