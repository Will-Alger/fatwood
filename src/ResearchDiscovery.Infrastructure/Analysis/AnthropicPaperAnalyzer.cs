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
/// Calls the Anthropic API (an inexpensive model — claude-haiku-4-5 by
/// default, since this runs once per paper across whole categories) to
/// produce the structured analysis JSON for a single paper. Structured
/// outputs enforce the schema server-side; policy declines are skipped, or
/// rescued by an optional configured fallback model.
/// </summary>
public class AnthropicPaperAnalyzer(
    AnthropicClient client,
    IOptions<AnalysisOptions> options,
    ILogger<AnthropicPaperAnalyzer> logger) : IPaperAnalyzer
{
    public async Task<PaperAnalysis?> AnalyzeAsync(Paper paper, CancellationToken ct)
    {
        var opts = options.Value;

        var schema = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
            AnalysisContract.SchemaJson)!;

        // Effort is only sent when configured (not every model supports it);
        // the fallback chain is only wired up when a fallback model is
        // configured — the default is no fallback, so a decline just skips
        // the paper, which costs nothing.
        var useFallback = opts.FallbackModel is { Length: > 0 };

        var parameters = new MessageCreateParams
        {
            Model = opts.Model,
            MaxTokens = opts.MaxOutputTokens,
            Betas = useFallback ? ["server-side-fallback-2026-06-01"] : null,
            Fallbacks = useFallback ? [new() { Model = opts.FallbackModel! }] : null,
            OutputConfig = opts.Effort is { Length: > 0 } effort
                ? new BetaOutputConfig
                {
                    Effort = effort,
                    Format = new BetaJsonOutputFormat { Schema = schema },
                }
                : new BetaOutputConfig
                {
                    Format = new BetaJsonOutputFormat { Schema = schema },
                },
            System = AnalysisContract.SystemPrompt,
            Messages =
            [
                new()
                {
                    Role = Role.User,
                    Content = AnalysisContract.BuildUserPrompt(
                        paper.ArxivId,
                        paper.Title,
                        paper.Authors,
                        paper.PrimaryCategory?.Code ?? "unknown",
                        paper.PaperCategories.Select(pc => pc.Category.Code),
                        paper.PublishedUtc,
                        paper.Abstract),
                },
            ],
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
            AnalysisOptions.CurrentSchemaVersion);
    }
}
