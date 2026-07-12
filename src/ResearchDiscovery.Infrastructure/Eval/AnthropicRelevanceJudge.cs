using System.Text.Json;
using Anthropic;
using Anthropic.Models.Beta.Messages;
using Microsoft.Extensions.Logging;
using ResearchDiscovery.Application.Abstractions;
using ResearchDiscovery.Application.Eval;
using ResearchDiscovery.Application.Options;

namespace ResearchDiscovery.Infrastructure.Eval;

/// <summary>
/// LLM call site #3 — eval-only. Grades (query, paper) pairs 0–3 in batches
/// so ~40 papers per query cost a handful of calls. The rubric deliberately
/// mirrors the product's exploration principle: unfamiliar tools never lower
/// a grade; only topical fit and buildability do.
/// </summary>
public class AnthropicRelevanceJudge(
    AnthropicClient client,
    ILlmSettingsService settings,
    ILlmUsageRecorder usage,
    ILogger<AnthropicRelevanceJudge> logger) : IRelevanceJudge
{
    private const int BatchSize = 10;
    private const int MaxConcurrentBatches = 4;
    private const int MaxOutputTokens = 4096;
    private const int MaxAbstractChars = 2000;

    // v2 (2026-07-12): added the disqualifying-constraints rule after sonnet
    // calibration caught v1 grading an already-open-source framework 3 on a
    // wants-no-code query. Bumping this forces a full `eval regrade`.
    public int RubricVersion => 2;

    private const string SchemaJson = """
    {
      "type": "object",
      "additionalProperties": false,
      "required": ["judgments"],
      "properties": {
        "judgments": {
          "type": "array",
          "items": {
            "type": "object",
            "additionalProperties": false,
            "required": ["arxiv_id", "grade", "rationale"],
            "properties": {
              "arxiv_id": { "type": "string", "description": "Exactly the id given for the paper." },
              "grade": { "type": "integer", "description": "0, 1, 2, or 3 per the rubric." },
              "rationale": { "type": "string", "description": "One short sentence justifying the grade." }
            }
          }
        }
      }
    }
    """;

    private const string SystemPrompt = """
        You are a relevance judge for a research-paper discovery tool. The searcher is an
        engineer looking for arXiv papers that could become buildable portfolio projects.
        Grade how well each paper serves the searcher's stated intent:

        3 = excellent: directly on-target for the intent AND plausibly buildable as an
            individual portfolio project (clear method, reasonable scope or data access).
        2 = relevant: clearly serves the intent; a good candidate worth the searcher's time.
        1 = tangential: shares a domain or method but would not satisfy the intent.
        0 = irrelevant to the intent.

        Rules:
        - FIRST check for DISQUALIFYING constraints in the intent and grade them strictly:
          if the searcher explicitly wants papers WITHOUT public code, a paper whose
          title/abstract advertises released code or an existing open-source framework is
          at most grade 1 no matter how on-topic it is. The same applies to any other
          explicit exclusion or hard requirement the searcher states (hardware limits,
          data-access needs, recency).
        - Judge against the searcher's INTENT (query + persona), not against their current
          skills: an on-topic paper using unfamiliar languages or frameworks is NOT graded
          down for that — skills are learnable.
        - Grade purely from the paper's title and abstract; do not assume content beyond them.
        - Return exactly one judgment per paper, using the exact arxiv_id provided.
        """;

    public async Task<RelevanceJudgeResult> JudgeAsync(
        EvalQuery query,
        IReadOnlyList<JudgeCandidate> candidates,
        CancellationToken ct,
        string? modelOverride = null)
    {
        var modelId = modelOverride
            ?? (await settings.GetModelForStepAsync(LlmOptions.StepRelevanceJudge, ct)).Id;
        var schema = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(SchemaJson)!;

        var batches = candidates
            .Select((c, i) => (Candidate: c, Index: i))
            .GroupBy(x => x.Index / BatchSize)
            .Select(g => g.Select(x => x.Candidate).ToList())
            .ToList();

        var verdicts = new List<RelevanceVerdict>[batches.Count];

        await Parallel.ForEachAsync(
            Enumerable.Range(0, batches.Count),
            new ParallelOptions { MaxDegreeOfParallelism = MaxConcurrentBatches, CancellationToken = ct },
            async (i, token) =>
            {
                verdicts[i] = await JudgeBatchAsync(modelId, schema, query, batches[i], token);
            });

        var all = verdicts.SelectMany(v => v).ToList();
        logger.LogInformation(
            "Judged {Count}/{Total} papers for eval query {QueryId} via {Model}",
            all.Count, candidates.Count, query.Id, modelId);

        return new RelevanceJudgeResult(modelId, all);
    }

    private async Task<List<RelevanceVerdict>> JudgeBatchAsync(
        string modelId,
        Dictionary<string, JsonElement> schema,
        EvalQuery query,
        IReadOnlyList<JudgeCandidate> batch,
        CancellationToken ct)
    {
        var papersBlock = string.Join("\n\n", batch.Select(p =>
            $"""
             arxiv_id: {p.ArxivId}
             Title: {p.Title}
             Abstract: {Truncate(p.Abstract)}
             """));

        var response = await client.Beta.Messages.Create(new MessageCreateParams
        {
            Model = modelId,
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
                        Searcher persona:
                        {query.Persona}

                        Search intent (the searcher's own words):
                        {query.Query}

                        Papers to judge ({batch.Count}):

                        {papersBlock}
                        """,
                },
            ],
        }, cancellationToken: ct);

        // Judge runs are CLI-only: the usage context has no user, so these
        // land as system rows — ops visibility without billing anyone.
        await usage.RecordAsync(
            LlmOptions.StepRelevanceJudge, modelId,
            response.Usage?.InputTokens ?? 0, response.Usage?.OutputTokens ?? 0, ct);

        if ($"{response.StopReason}" == "refusal")
        {
            throw new InvalidOperationException(
                $"The model declined to judge eval query '{query.Id}'.");
        }

        var json = response.Content
            .Select(block => block.TryPickText(out BetaTextBlock? text) ? text.Text : null)
            .FirstOrDefault(t => t is not null)
            ?? throw new InvalidOperationException("Relevance judging returned no content.");

        return ParseVerdicts(json, batch.Select(b => b.ArxivId).ToHashSet(StringComparer.Ordinal));
    }

    internal static List<RelevanceVerdict> ParseVerdicts(string json, IReadOnlySet<string> expectedIds)
    {
        using var doc = JsonDocument.Parse(json);
        var verdicts = new List<RelevanceVerdict>();

        foreach (var item in doc.RootElement.GetProperty("judgments").EnumerateArray())
        {
            var id = item.GetProperty("arxiv_id").GetString();
            if (id is null || !expectedIds.Contains(id))
            {
                continue; // hallucinated or repeated id — drop rather than poison the artifact
            }

            verdicts.Add(new RelevanceVerdict(
                id,
                Math.Clamp(item.GetProperty("grade").GetInt32(), 0, 3),
                item.GetProperty("rationale").GetString() ?? string.Empty));
        }

        return verdicts
            .GroupBy(v => v.ArxivId, StringComparer.Ordinal)
            .Select(g => g.First())
            .ToList();
    }

    private static string Truncate(string text) =>
        text.Length <= MaxAbstractChars ? text : text[..MaxAbstractChars] + "…";
}
