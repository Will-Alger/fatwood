using System.Text.Json;
using Anthropic;
using Anthropic.Models.Beta.Messages;
using Microsoft.Extensions.Logging;
using ResearchDiscovery.Application.Abstractions;
using ResearchDiscovery.Application.Options;
using ResearchDiscovery.Infrastructure.Arxiv;

namespace ResearchDiscovery.Infrastructure.Search;

/// <summary>
/// LLM call site #1: one cheap structured-output call per search that turns
/// natural-language intent into a deterministic SearchPlan. This is the ONLY
/// place a model sees the query; it never sees the corpus. The critical
/// expansion job: career intent ("move into applied ML") → research topics
/// an embedding model can match against abstracts.
/// </summary>
public class AnthropicSearchPlanCompiler(
    Llm.AnthropicCallFactory callFactory,
    ILlmUsageRecorder usage,
    ILogger<AnthropicSearchPlanCompiler> logger) : ISearchPlanCompiler
{
    private const int MaxOutputTokens = 2048;

    private const string SchemaJson = """
    {
      "type": "object",
      "additionalProperties": false,
      "required": ["interpretation", "anchor_text", "categories", "date_window_days", "require_no_code", "hypothetical_abstract", "query_style"],
      "properties": {
        "interpretation": {
          "type": "string",
          "description": "One sentence, shown to the user above the results: what you understood them to be looking for."
        },
        "anchor_text": {
          "type": "string",
          "description": "A dense comma-separated list of 8-15 concrete research topics, methods, and problem domains that papers matching this search would discuss in their abstracts. This is embedded and cosine-matched against paper abstracts - write abstract-vocabulary, not career-vocabulary. Expand career goals into the research areas that serve them."
        },
        "categories": {
          "type": "array",
          "items": { "type": "string" },
          "description": "arXiv category codes to search within, chosen ONLY from the provided known list. Empty array = all categories."
        },
        "date_window_days": {
          "type": ["integer", "null"],
          "description": "Restrict to papers from the last N days, or null for no restriction. Only set when the query implies recency."
        },
        "require_no_code": {
          "type": ["boolean", "null"],
          "description": "true only when the user explicitly wants papers WITHOUT existing public code (reproduction-gap hunting). Otherwise null."
        },
        "hypothetical_abstract": {
          "type": "string",
          "description": "The abstract of the hypothetical IDEAL paper for this search, 4-6 sentences, written exactly like a real arXiv abstract: the problem, the proposed method, key results. Dense and technical, no meta-commentary, no mention of the user. This is embedded and matched against real abstracts (abstracts match abstracts far better than topic lists do)."
        },
        "query_style": {
          "type": "string",
          "enum": ["precise", "exploratory", "mixed"],
          "description": "How the query is phrased. 'precise': it names specific methods, systems, model families, or acronyms - the user's exact words are the best possible search terms (e.g. 'speculative decoding', 'DPO vs RLHF'). 'exploratory': goal- or career-phrased, or plain-language descriptions where the user's vocabulary likely differs from paper vocabulary (e.g. 'something impressive to build', 'chatbots making things up'). 'mixed': both, or unclear."
        }
      }
    }
    """;

    private const string SystemPrompt =
        "You compile natural-language search intent into a structured plan for a research-paper " +
        "discovery tool. The user browses arXiv papers looking for buildable portfolio projects. " +
        "Your anchor_text drives an embedding similarity match against paper abstracts, so " +
        "translate career goals and vague intent into the concrete research topics, methods, and " +
        "problem domains that relevant abstracts would actually contain. " +
        "The corpus spans many fields, so category selection is how a search lands in the right " +
        "FIELD: infer the field(s) from the user's intent and profile, not just from keywords — a " +
        "pre-med student's 'resume projects' wants quantitative biology and medical imaging, an " +
        "audio engineer's wants sound and speech processing, a backend engineer's wants distributed " +
        "systems and databases. Choose categories only from the provided known list. Pick narrowly " +
        "when the query names a field; include every plausibly relevant category when intent spans " +
        "fields; and return an EMPTY list for genuinely cross-domain queries — empty deliberately " +
        "searches everything and is often right. Never invent filters the user didn't imply.";

    public async Task<SearchPlan> CompileAsync(
        string query,
        string? profile,
        IReadOnlyList<string> knownCategories,
        CancellationToken ct)
    {
        var (client, model) = await callFactory.ResolveAsync(LlmOptions.StepQueryCompiler, ct);
        var schema = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(SchemaJson)!;

        var profileBlock = string.IsNullOrWhiteSpace(profile)
            ? string.Empty
            : $"""

               The user's saved profile (context for interpreting the query; the query wins on conflict):
               {profile}
               """;

        var response = await client.Beta.Messages.Create(new MessageCreateParams
        {
            Model = model.Id,
            MaxTokens = MaxOutputTokens,
            OutputConfig = new BetaOutputConfig
            {
                Format = new BetaJsonOutputFormat { Schema = schema },
            },
            System = SystemPrompt,
            Messages =
            [
                new()
                {
                    Role = Role.User,
                    Content = $"""
                        Known categories (choose only from these codes):
                        {FormatKnownCategories(knownCategories)}
                        {profileBlock}
                        Search query:
                        {query}
                        """,
                },
            ],
        }, cancellationToken: ct);

        await usage.RecordAsync(
            LlmOptions.StepQueryCompiler, model.Id,
            response.Usage?.InputTokens ?? 0, response.Usage?.OutputTokens ?? 0, ct);

        if ($"{response.StopReason}" == "refusal")
        {
            throw new InvalidOperationException("The model declined to compile this search query.");
        }

        var json = response.Content
            .Select(block => block.TryPickText(out BetaTextBlock? text) ? text.Text : null)
            .FirstOrDefault(t => t is not null)
            ?? throw new InvalidOperationException("Query compilation returned no content.");

        var plan = ParsePlan(json, knownCategories);
        logger.LogInformation(
            "Compiled search via {Model}: {Interpretation}", model.Id, plan.Interpretation);
        return plan;
    }

    /// <summary>
    /// One "code - Display Name" line per known category, sorted, so the
    /// model can infer FIELDS from intent instead of pattern-matching bare
    /// codes. Unknown codes fall back to the code itself.
    /// </summary>
    internal static string FormatKnownCategories(IReadOnlyList<string> knownCategories) =>
        string.Join("\n", knownCategories
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(c => c, StringComparer.Ordinal)
            .Select(c => $"{c} - {ArxivCategoryNames.DisplayNameFor(c)}"));

    internal static SearchPlan ParsePlan(string json, IReadOnlyList<string> knownCategories)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var known = knownCategories.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var categories = root.GetProperty("categories").EnumerateArray()
            .Select(c => c.GetString())
            .Where(c => c is not null && known.Contains(c))
            .Select(c => c!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var anchor = root.GetProperty("anchor_text").GetString();
        if (string.IsNullOrWhiteSpace(anchor))
        {
            throw new InvalidOperationException("Compiled plan has an empty anchor_text.");
        }

        var hyde = root.TryGetProperty("hypothetical_abstract", out var hydeProp)
            && hydeProp.ValueKind == JsonValueKind.String
                ? hydeProp.GetString()
                : null;

        var intent = root.TryGetProperty("query_style", out var intentProp)
            && intentProp.ValueKind == JsonValueKind.String
                ? intentProp.GetString()?.Trim().ToLowerInvariant()
                : null;
        if (intent is not ("precise" or "exploratory" or "mixed"))
        {
            intent = null; // unknown values degrade to "no signal", never throw
        }

        return new SearchPlan(
            root.GetProperty("interpretation").GetString() ?? string.Empty,
            anchor,
            categories,
            root.GetProperty("date_window_days").ValueKind == JsonValueKind.Number
                ? root.GetProperty("date_window_days").GetInt32()
                : null,
            root.GetProperty("require_no_code").ValueKind == JsonValueKind.True
                ? true
                : null,
            string.IsNullOrWhiteSpace(hyde) ? null : hyde,
            intent);
    }
}
