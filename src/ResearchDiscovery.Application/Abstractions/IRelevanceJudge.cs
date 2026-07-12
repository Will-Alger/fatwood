using ResearchDiscovery.Application.Eval;

namespace ResearchDiscovery.Application.Abstractions;

public sealed record JudgeCandidate(string ArxivId, string Title, string Abstract);

public sealed record RelevanceVerdict(string ArxivId, int Grade, string Rationale);

public sealed record RelevanceJudgeResult(
    string ModelId,
    IReadOnlyList<RelevanceVerdict> Verdicts);

/// <summary>
/// LLM call site #3 (eval-only, never in the product path): grades how well a
/// paper serves an eval query's intent, producing the ground truth the offline
/// ranking metrics are computed against.
/// </summary>
public interface IRelevanceJudge
{
    /// <summary>Rubric version stamped into judgment artifacts; bump when the grading prompt changes materially.</summary>
    int RubricVersion { get; }

    /// <param name="modelOverride">Explicit judge model (calibration runs);
    /// null = the configured RelevanceJudge step model.</param>
    Task<RelevanceJudgeResult> JudgeAsync(
        EvalQuery query,
        IReadOnlyList<JudgeCandidate> candidates,
        CancellationToken ct,
        string? modelOverride = null);
}
