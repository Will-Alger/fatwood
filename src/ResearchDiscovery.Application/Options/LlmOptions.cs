using System.ComponentModel.DataAnnotations;

namespace ResearchDiscovery.Application.Options;

/// <summary>
/// Config-driven LLM model registry: the allowlist of models the UI can
/// assign to pipeline steps, with pricing metadata for live cost estimates.
/// Adding a model is an appsettings entry, never a code change.
/// </summary>
public class LlmOptions
{
    public const string SectionName = "Llm";

    /// <summary>Step name for the natural-language query compiler.</summary>
    public const string StepQueryCompiler = "QueryCompiler";

    /// <summary>Step name for the per-paper personalized analysis.</summary>
    public const string StepPaperAnalysis = "PaperAnalysis";

    /// <summary>Step name for the offline eval relevance judge (never in the product path).</summary>
    public const string StepRelevanceJudge = "RelevanceJudge";

    public static readonly IReadOnlyList<string> Steps =
        [StepQueryCompiler, StepPaperAnalysis, StepRelevanceJudge];

    [MinLength(1)]
    public IReadOnlyList<LlmModel> Models { get; set; } = [];

    /// <summary>Default model per step; overridable at runtime via the DB-backed settings.</summary>
    [Required]
    public Dictionary<string, string> Defaults { get; set; } = [];

    public sealed class LlmModel
    {
        [Required]
        public string Id { get; set; } = string.Empty;

        [Required]
        public string DisplayName { get; set; } = string.Empty;

        /// <summary>USD per million input tokens.</summary>
        [Range(0, 1000)]
        public decimal InputPerMTok { get; set; }

        /// <summary>USD per million output tokens.</summary>
        [Range(0, 1000)]
        public decimal OutputPerMTok { get; set; }

        /// <summary>
        /// Premium models never run on the platform key: without a BYO key the
        /// call silently falls back to the step's default model.
        /// </summary>
        public bool RequiresByoKey { get; set; }
    }
}
