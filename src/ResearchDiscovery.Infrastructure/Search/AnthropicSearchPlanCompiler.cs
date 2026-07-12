using System.Text.Json;
using Anthropic;
using Anthropic.Models.Beta.Messages;
using Microsoft.Extensions.Logging;
using ResearchDiscovery.Application.Abstractions;
using ResearchDiscovery.Application.Options;

namespace ResearchDiscovery.Infrastructure.Search;

/// <summary>
/// LLM call site #1: one cheap structured-output call per search that turns
/// natural-language intent into a deterministic SearchPlan. This is the ONLY
/// place a model sees the query; it never sees the corpus. The critical
/// expansion job: career intent ("move into applied ML") → research topics
/// an embedding model can match against abstracts.
/// </summary>
public class AnthropicSearchPlanCompiler(
    AnthropicClient client,
    ILlmSettingsService settings,
    ILlmUsageRecorder usage,
    ILogger<AnthropicSearchPlanCompiler> logger) : ISearchPlanCompiler
{
    private const int MaxOutputTokens = 2048;

    private const string SchemaJson = """
    {
      "type": "object",
      "additionalProperties": false,
      "required": ["interpretation", "anchor_text", "categories", "date_window_days", "require_no_code"],
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
        }
      }
    }
    """;

    private const string SystemPrompt =
        "You compile natural-language search intent into a structured plan for a research-paper " +
        "discovery tool. The user browses recent arXiv papers looking for buildable portfolio " +
        "projects. Your anchor_text drives an embedding similarity match against paper abstracts, " +
        "so translate career goals and vague intent into the concrete research topics, methods, and " +
        "problem domains that relevant abstracts would actually contain. Choose categories only " +
        "from the provided known list. Do not narrow more than the query warrants: when in doubt, " +
        "include more categories and leave filters null. Never invent filters the user didn't imply.";

    public async Task<SearchPlan> CompileAsync(
        string query,
        string? profile,
        IReadOnlyList<string> knownCategories,
        CancellationToken ct)
    {
        var model = await settings.GetModelForStepAsync(LlmOptions.StepQueryCompiler, ct);
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
                        Known category codes (choose only from these): {string.Join(", ", knownCategories)}
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

        return new SearchPlan(
            root.GetProperty("interpretation").GetString() ?? string.Empty,
            anchor,
            categories,
            root.GetProperty("date_window_days").ValueKind == JsonValueKind.Number
                ? root.GetProperty("date_window_days").GetInt32()
                : null,
            root.GetProperty("require_no_code").ValueKind == JsonValueKind.True
                ? true
                : null);
    }
}
