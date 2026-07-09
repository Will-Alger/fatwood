namespace ResearchDiscovery.Infrastructure.Analysis;

/// <summary>
/// The v2 analysis contract: personalized paper × person evaluation. The JSON
/// schema is enforced via structured outputs; version changes here must bump
/// AnalysisOptions.CurrentSchemaVersion (v1 rows then re-analyze on demand).
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
        "hard_blockers",
        "learning_bridge",
        "estimated_effort",
        "approach",
        "approach_rationale",
        "reference_code_likelihood",
        "goal_alignment_score",
        "resume_signal",
        "extension_idea",
        "required_skills",
        "composite_score"
      ],
      "properties": {
        "summary": {
          "type": "string",
          "description": "Two or three plain-English sentences: what the paper contributes and why this builder might care."
        },
        "feasibility_score": {
          "type": "integer",
          "description": "0-10. Feasibility for THIS person, judging only hard blockers: specialized hardware (GPU clusters, lab equipment), proprietary or inaccessible datasets, months of prerequisite theory. Unfamiliar languages, frameworks, or tools a working engineer learns in days are NOT blockers and must not lower this score. 0 = genuinely impossible for them, 10 = they could start this weekend."
        },
        "hard_blockers": {
          "type": "array",
          "items": { "type": "string" },
          "description": "Genuine hard blockers only (hardware, data access, deep prerequisite theory measured in months). Empty array when none. Never list learnable tech here."
        },
        "learning_bridge": {
          "type": "string",
          "description": "One or two sentences: what this person would need to learn to start, roughly how long that takes given their background, and which of their existing skills carry over. Frame unfamiliarity as an on-ramp, not a penalty."
        },
        "estimated_effort": {
          "type": "string",
          "enum": ["weekend", "one_to_two_weeks", "about_a_month", "multi_month"],
          "description": "Realistic effort for THIS person to reach a demonstrable result (not publication parity), including the learning bridge."
        },
        "approach": {
          "type": "string",
          "enum": ["reproduce", "extend"],
          "description": "Whether the better portfolio move is to reproduce the paper's result or to extend/apply it to a new setting."
        },
        "approach_rationale": {
          "type": "string",
          "description": "One or two sentences on why that approach is the better artifact for this person's goals."
        },
        "reference_code_likelihood": {
          "type": "string",
          "enum": ["high", "medium", "low"],
          "description": "Likelihood that reference code exists publicly, judged from venue, category norms, and abstract. If the metadata already includes an advertised code URL, answer high."
        },
        "goal_alignment_score": {
          "type": "integer",
          "description": "0-10. How directly a completed project from this paper advances the person's STATED goals (target domain, role, location). 0 = unrelated to their goals, 10 = exactly the work their target role does."
        },
        "resume_signal": {
          "type": "string",
          "description": "One or two sentences: the story this project tells on THIS person's resume, bridging from their existing experience toward their target role."
        },
        "extension_idea": {
          "type": "string",
          "description": "One concrete, scoped extension THIS person could build, playing to their existing strengths where possible - specific enough to start this week."
        },
        "required_skills": {
          "type": "array",
          "items": { "type": "string" },
          "description": "3-8 concrete skills or technologies the project would exercise, noting which the person already has vs would newly learn (e.g. 'REST APIs (have)', 'PyTorch (new)')."
        },
        "composite_score": {
          "type": "number",
          "description": "0-100 overall suitability as this person's next portfolio project, weighing feasibility-for-them, effort, goal alignment, and resume signal. Calibrate: 80+ exceptional, 60-79 strong, 40-59 workable, below 40 poor. Most papers are NOT strong candidates."
        }
      }
    }
    """;

    /// <summary>
    /// Stable, person-agnostic judging rules. The person and the paper both go
    /// in the user turn so this prefix stays cacheable.
    /// </summary>
    public const string SystemPrompt =
        "You evaluate arXiv papers as candidate portfolio projects for a specific software " +
        "engineer whose experience and goals are provided with each paper. Judge the pairing, " +
        "not the paper in isolation: the same paper can be a poor project for one person and an " +
        "excellent one for another. Rules: (1) Language, framework, and tooling differences are " +
        "never feasibility blockers - anything a working engineer learns in days is neutral, and " +
        "a modest learning bridge is a feature of a portfolio project, not a defect. Flag only " +
        "hard blockers: specialized hardware, proprietary or inaccessible data, or prerequisite " +
        "theory measured in months. (2) Goal alignment is judged against the person's stated " +
        "goals, not any fixed domain. (3) You see only metadata and abstract, never full text - " +
        "judge from what is stated and category norms; do not invent results. (4) Be decisive " +
        "and calibrated: reserve high composite scores for genuinely strong pairings. Fill every " +
        "field of the required JSON schema.";

    public static string BuildUserPrompt(
        string? profileDescription,
        string arxivId,
        string title,
        string authors,
        string primaryCategory,
        IEnumerable<string> categories,
        DateTimeOffset publishedUtc,
        string? codeUrl,
        string @abstract)
    {
        var person = string.IsNullOrWhiteSpace(profileDescription)
            ? "(No profile provided - assume a generalist software engineer with a few years of professional experience.)"
            : profileDescription;

        var code = codeUrl is null
            ? "none advertised"
            : codeUrl;

        return $"""
            Evaluate this paper as the next portfolio project for this person.

            THE PERSON:
            {person}

            THE PAPER:
            arXiv ID: {arxivId}
            Title: {title}
            Authors: {authors}
            Primary category: {primaryCategory}
            All categories: {string.Join(", ", categories)}
            Published: {publishedUtc:yyyy-MM-dd}
            Advertised code repository: {code}

            Abstract:
            {@abstract}
            """;
    }
}
