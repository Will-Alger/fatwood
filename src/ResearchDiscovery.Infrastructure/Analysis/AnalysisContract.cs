namespace ResearchDiscovery.Infrastructure.Analysis;

/// <summary>
/// The v1 analysis contract: the JSON schema enforced via structured outputs
/// and the prompts that produce it. Version changes here must bump
/// AnalysisOptions.CurrentSchemaVersion.
/// </summary>
public static class AnalysisContract
{
    /// <summary>
    /// Structured-outputs schema. Numeric ranges live in descriptions because
    /// structured outputs do not support minimum/maximum constraints.
    /// </summary>
    public const string SchemaJson = """
    {
      "type": "object",
      "additionalProperties": false,
      "required": [
        "summary",
        "feasibility_score",
        "feasibility_rationale",
        "estimated_effort",
        "approach",
        "approach_rationale",
        "reference_code_likelihood",
        "resume_signal",
        "fintech_relevance_score",
        "extension_idea",
        "required_skills",
        "composite_score"
      ],
      "properties": {
        "summary": {
          "type": "string",
          "description": "Two or three plain-English sentences: what the paper contributes and why a builder might care."
        },
        "feasibility_score": {
          "type": "integer",
          "description": "0-10. How feasible is it for one experienced software engineer to implement the core idea from the abstract alone, without exotic hardware or private datasets? 0 = requires a lab, 10 = a laptop weekend project."
        },
        "feasibility_rationale": {
          "type": "string",
          "description": "One or two sentences justifying feasibility_score, naming the main obstacle if any (compute, data access, specialized theory)."
        },
        "estimated_effort": {
          "type": "string",
          "enum": ["weekend", "one_to_two_weeks", "about_a_month", "multi_month"],
          "description": "Realistic solo effort to reach a demonstrable result (not publication parity)."
        },
        "approach": {
          "type": "string",
          "enum": ["reproduce", "extend"],
          "description": "Whether the better portfolio move is to reproduce the paper's result or to extend/apply it to a new setting."
        },
        "approach_rationale": {
          "type": "string",
          "description": "One or two sentences on why that approach is the better resume artifact for this paper."
        },
        "reference_code_likelihood": {
          "type": "string",
          "enum": ["high", "medium", "low"],
          "description": "Likelihood that reference code already exists publicly (official repo or community implementation), judged from the venue, category norms, and abstract."
        },
        "resume_signal": {
          "type": "string",
          "description": "One or two sentences: what completing this project signals to a hiring manager, and for which kind of role."
        },
        "fintech_relevance_score": {
          "type": "integer",
          "description": "0-10. Relevance to fintech / quantitative-finance roles specifically. 0 = none, 10 = directly a trading/risk/markets problem."
        },
        "extension_idea": {
          "type": "string",
          "description": "One concrete, scoped extension a solo developer could build on top of this paper - specific enough to start this week."
        },
        "required_skills": {
          "type": "array",
          "items": { "type": "string" },
          "description": "3-8 concrete skills or technologies the project would exercise (e.g. 'PyTorch', 'order-book data', 'CUDA', 'Rust')."
        },
        "composite_score": {
          "type": "number",
          "description": "0-100 overall suitability as a solo portfolio project, weighing feasibility, effort, reference-code availability, and resume signal. Calibrate: 80+ exceptional, 60-79 strong, 40-59 workable, below 40 poor."
        }
      }
    }
    """;

    /// <summary>
    /// Kept deliberately stable and free of per-request content: the goal and
    /// judging criteria only. Per-paper content goes in the user turn.
    /// </summary>
    public const string SystemPrompt =
        "You evaluate recent arXiv papers as candidate portfolio projects for an experienced " +
        "software engineer who wants to build something real from research - typically to " +
        "strengthen a resume for fintech and NYC engineering roles. You are given only the " +
        "paper's metadata and abstract, never the full text; judge from what is stated and " +
        "from category norms, and do not invent results the abstract does not claim. Be " +
        "decisive and calibrated: most papers are NOT good solo projects, so reserve high " +
        "composite scores for papers that are genuinely feasible, well scoped, and legible " +
        "to a hiring manager. Fill every field of the required JSON schema.";

    public static string BuildUserPrompt(
        string arxivId,
        string title,
        string authors,
        string primaryCategory,
        IEnumerable<string> categories,
        DateTimeOffset publishedUtc,
        string @abstract) =>
        $"""
        Evaluate this paper as a solo portfolio project.

        arXiv ID: {arxivId}
        Title: {title}
        Authors: {authors}
        Primary category: {primaryCategory}
        All categories: {string.Join(", ", categories)}
        Published: {publishedUtc:yyyy-MM-dd}

        Abstract:
        {@abstract}
        """;
}
