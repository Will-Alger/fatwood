using ResearchDiscovery.Application.Abstractions;
using ResearchDiscovery.Infrastructure.DependencyInjection;
using ResearchDiscovery.Infrastructure.Persistence;

namespace ResearchDiscovery.Api.Cli;

/// <summary>
/// One-shot embedding backfill: `dotnet ResearchDiscovery.Api.dll embed`.
/// Embeds every paper without a current-model vector using the local ONNX
/// model — zero API tokens. Safe to re-run; already-embedded papers are
/// skipped by the selection query.
/// </summary>
public static class EmbedCommandRunner
{
    public static async Task<int> RunAsync()
    {
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

        var embeddings = host.Services.GetRequiredService<IPaperEmbeddingService>();

        try
        {
            var summary = await embeddings.EmbedMissingAsync(cts.Token);
            Console.WriteLine(
                $"Embedding run finished: embedded {summary.Embedded}, failed {summary.Failed}.");
            return summary.Failed == 0 ? 0 : 1;
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            Console.Error.WriteLine("Embedding run cancelled (progress is saved).");
            return 1;
        }
    }
}
