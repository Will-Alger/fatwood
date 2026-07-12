using Anthropic;
using Microsoft.Extensions.Options;
using ResearchDiscovery.Application.Abstractions;
using ResearchDiscovery.Application.Options;

namespace ResearchDiscovery.Infrastructure.Llm;

/// <summary>One resolved Anthropic call setup: which client (platform vs BYO key) and which model.</summary>
public sealed record AnthropicCall(AnthropicClient Client, LlmOptions.LlmModel Model);

/// <summary>
/// The single decision point for "whose key, which model" on every LLM call:
/// - the scope's user has a BYO key → their key pays, and the usage context
///   is flagged so the ledger never debits platform budget for it;
/// - the configured model is premium (RequiresByoKey) but there is no BYO
///   key → fall back to the step's default model rather than spending
///   platform money on premium tokens.
/// </summary>
public class AnthropicCallFactory(
    AnthropicClient platformClient,
    ILlmSettingsService settings,
    ILlmUsageContext usageContext,
    IUserKeyService userKeys,
    IOptions<LlmOptions> llmOptions)
{
    public async Task<AnthropicCall> ResolveAsync(string step, CancellationToken ct)
    {
        var model = await settings.GetModelForStepAsync(step, ct);

        string? byoKey = null;
        if (usageContext.UserId is { } userId)
        {
            byoKey = await userKeys.GetDecryptedAsync(userId, ct);
        }

        if (byoKey is null && model.RequiresByoKey)
        {
            var options = llmOptions.Value;
            var fallbackId = options.Defaults[step];
            model = options.Models.First(m =>
                string.Equals(m.Id, fallbackId, StringComparison.OrdinalIgnoreCase));
        }

        usageContext.UsedByoKey = byoKey is not null;

        var client = byoKey is null
            ? platformClient
            : platformClient.WithOptions(o => o with { ApiKey = byoKey });

        return new AnthropicCall((AnthropicClient)client, model);
    }
}
