using ResearchDiscovery.Infrastructure.DependencyInjection;
using ResearchDiscovery.Infrastructure.Enrichment;
using ResearchDiscovery.Infrastructure.Persistence;

namespace ResearchDiscovery.Api.Cli;

/// <summary>
/// One-shot signal enrichment (citations + optional GitHub stars) — ops-only,
/// same posture as ingest/analyze/embed. `enrich` fetches citations;
/// `enrich --stars` also fetches stars when GITHUB_TOKEN is set.
/// </summary>
public static class EnrichCommandRunner
{
    public static async Task<int> RunAsync(string[] args)
    {
        var includeStars = args.Contains("--stars", StringComparer.OrdinalIgnoreCase);

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddInfrastructure(builder.Configuration);

        using var host = builder.Build();
        await DatabaseStartup.MigrateIfConfiguredAsync(host.Services, builder.Configuration, cts.Token);

        var enricher = host.Services.GetRequiredService<PaperSignalEnricher>();
        try
        {
            var summary = await enricher.EnrichAsync(includeStars, cts.Token);
            Console.WriteLine(
                $"Enrichment finished: {summary.CitationsFetched} citation record(s), " +
                $"{summary.StarsFetched} starred repo(s), {summary.Failed} failure(s).");
            return 0;
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            Console.Error.WriteLine("Enrichment cancelled; completed batches were kept.");
            return 1;
        }
    }
}
