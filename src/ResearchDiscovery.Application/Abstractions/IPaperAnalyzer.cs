using ResearchDiscovery.Domain.Entities;

namespace ResearchDiscovery.Application.Abstractions;

/// <summary>
/// Structured analysis of a single paper × person pairing, ready to persist.
/// </summary>
public sealed record PaperAnalysis(
    string ResultJson,
    decimal CompositeScore,
    string Model,
    int SchemaVersion,
    int ProfileVersion);

/// <summary>
/// Produces the LLM analysis for one paper against the user's profile.
/// Separated from the run orchestration (<see cref="IAnalysisService"/>) so
/// tests can exercise selection/persistence without an Anthropic dependency.
/// </summary>
public interface IPaperAnalyzer
{
    /// <summary>
    /// Analyzes a single paper for the given person. Returns null when the
    /// model (and its fallback) declined the request — the paper is skipped,
    /// not an error.
    /// </summary>
    /// <param name="profileDescription">Prompt-ready profile text, or null when no profile exists.</param>
    /// <param name="profileVersion">Profile version stamped into the result for cache staleness (0 = no profile).</param>
    Task<PaperAnalysis?> AnalyzeAsync(
        Paper paper, string? profileDescription, int profileVersion, CancellationToken ct);
}
