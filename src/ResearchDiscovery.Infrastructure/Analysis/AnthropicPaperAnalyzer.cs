using System.Text.Json;
using Anthropic;
using Anthropic.Models.Beta.Messages;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ResearchDiscovery.Application.Abstractions;
using ResearchDiscovery.Application.Options;
using ResearchDiscovery.Domain.Entities;

namespace ResearchDiscovery.Infrastructure.Analysis;

/// <summary>
/// LLM call site #2: the personalized per-paper analysis. The model comes from
/// the UI-configurable per-step settings (default: a cheap model — this runs
/// once per paper over ranked slices). Structured outputs enforce the schema
/// server-side; policy declines are counted and skipped, or rescued by an
/// optional configured fallback model.
/// </summary>
public class AnthropicPaperAnalyzer(
    AnthropicClient client,
    ILlmSettingsService settings,
    IOptions<AnalysisOptions> options,
    ILogger<AnthropicPaperAnalyzer> logger) : IPaperAnalyzer
{
    public async Task<PaperAnalysis?> AnalyzeAsync(
        Paper paper, string? profileDescription, int profileVersion, CancellationToken ct)
    {
        var opts = options.Value;
        var model = await settings.GetModelForStepAsync(LlmOptions.StepPaperAnalysis, ct);

        var schema = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
            AnalysisContract.SchemaJson)!;

        var useFallback = opts.FallbackModel is { Length: > 0 };

        var outputConfig = opts.Effort is { Length: > 0 } effort
            ? new BetaOutputConfig
            {
                Effort = effort,
                Format = new BetaJsonOutputFormat { Schema = schema },
            }
            : new BetaOutputConfig
            {
                Format = new BetaJsonOutputFormat { Schema = schema },
            };

        List<BetaMessageParam> messages =
        [
            new()
            {
                Role = Role.User,
                Content = AnalysisContract.BuildUserPrompt(
                    profileDescription,
                    paper.ArxivId,
                    paper.Title,
                    paper.Authors,
                    paper.PrimaryCategory?.Code ?? "unknown",
                    paper.PaperCategories.Select(pc => pc.Category.Code),
                    paper.PublishedUtc,
                    paper.CodeUrl,
                    paper.Abstract),
            },
        ];

        // The fallback fields are only present when a fallback is configured:
        // an assigned property is serialized even when null, and the API
        // rejects a `fallbacks` field without its beta flag.
        var parameters = useFallback
            ? new MessageCreateParams
            {
                Model = model.Id,
                MaxTokens = opts.MaxOutputTokens,
                OutputConfig = outputConfig,
                System = AnalysisContract.SystemPrompt,
                Messages = messages,
                Betas = ["server-side-fallback-2026-06-01"],
                Fallbacks = [new() { Model = opts.FallbackModel! }],
            }
            : new MessageCreateParams
            {
                Model = model.Id,
                MaxTokens = opts.MaxOutputTokens,
                OutputConfig = outputConfig,
                System = AnalysisContract.SystemPrompt,
                Messages = messages,
            };

        var response = await client.Beta.Messages.Create(parameters, cancellationToken: ct);

        // Check stop_reason before touching content: a refusal can arrive with
        // an empty content array. A refusal (from the model, or from the whole
        // chain when a fallback is configured) skips the paper rather than
        // failing the run.
        if ($"{response.StopReason}" == "refusal")
        {
            logger.LogWarning(
                "Analysis of {ArxivId} was declined by the model ({Category})",
                paper.ArxivId, response.StopDetails?.Category);
            return null;
        }

        if ($"{response.StopReason}" == "max_tokens")
        {
            throw new InvalidOperationException(
                $"Analysis of {paper.ArxivId} was truncated at {opts.MaxOutputTokens} output tokens.");
        }

        var resultJson = response.Content
            .Select(block => block.TryPickText(out BetaTextBlock? text) ? text.Text : null)
            .FirstOrDefault(t => t is not null)
            ?? throw new InvalidOperationException(
                $"Analysis of {paper.ArxivId} returned no text content (stop_reason: {response.StopReason}).");

        using var parsed = JsonDocument.Parse(resultJson);
        var compositeScore = Math.Clamp(
            parsed.RootElement.GetProperty("composite_score").GetDecimal(), 0m, 100m);

        // response.Model reports which model actually served the request
        // (the fallback's ID when the primary declined mid-chain).
        return new PaperAnalysis(
            resultJson,
            decimal.Round(compositeScore, 2),
            $"{response.Model}",
            AnalysisOptions.CurrentSchemaVersion,
            profileVersion);
    }
}
