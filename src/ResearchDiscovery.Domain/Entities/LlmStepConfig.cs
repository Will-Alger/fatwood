namespace ResearchDiscovery.Domain.Entities;

/// <summary>
/// Per-step LLM model assignment, editable from the UI. One row per pipeline
/// step ("QueryCompiler", "PaperAnalysis"). Absent rows fall back to the
/// configured defaults; model ids are validated against the config-driven
/// model registry before persisting.
/// </summary>
public class LlmStepConfig
{
    /// <summary>Step name, e.g. "QueryCompiler" or "PaperAnalysis".</summary>
    public required string Step { get; set; }

    public required string ModelId { get; set; }

    public DateTimeOffset UpdatedUtc { get; set; }
}
