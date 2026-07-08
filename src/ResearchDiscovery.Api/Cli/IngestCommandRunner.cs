using ResearchDiscovery.Application.Abstractions;
using ResearchDiscovery.Domain.Entities;
using ResearchDiscovery.Infrastructure.DependencyInjection;
using ResearchDiscovery.Infrastructure.Persistence;

namespace ResearchDiscovery.Api.Cli;

/// <summary>
/// One-shot CLI ingestion mode — the ops-facing manual trigger. Builds a
/// generic host with the same configuration and service composition as the web
/// app, runs a single ingestion, and exits. Regular users never reach this:
/// it is a process invocation, not an HTTP surface.
/// </summary>
public static class IngestCommandRunner
{
    private const int ExitOk = 0;
    private const int ExitRunFailed = 1;
    private const int ExitAlreadyRunning = 2;
    private const int ExitUsage = 64;

    public static async Task<int> RunAsync(string[] args)
    {
        var mode = args.Length > 1 ? args[1].ToLowerInvariant() : null;
        if (mode is not ("backfill" or "delta"))
        {
            Console.Error.WriteLine(
                "Usage: ingest backfill [--days <N>] [--max-per-category <N>] | ingest delta");
            return ExitUsage;
        }

        BackfillOverrides? overrides = null;
        if (mode == "backfill")
        {
            if (!TryParseBackfillOptions(args, out overrides))
            {
                return ExitUsage;
            }
        }

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        // Deliberately not forwarding args: the ingest verbs are not
        // configuration and would confuse the command-line config provider.
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddInfrastructure(builder.Configuration);

        using var host = builder.Build();
        await DatabaseStartup.MigrateIfConfiguredAsync(host.Services, builder.Configuration, cts.Token);

        await using var scope = host.Services.CreateAsyncScope();
        var ingestion = scope.ServiceProvider.GetRequiredService<IIngestionService>();

        try
        {
            var summary = mode == "backfill"
                ? await ingestion.RunBackfillAsync(overrides, cts.Token)
                : await ingestion.RunDeltaAsync(cts.Token);

            Console.WriteLine(
                $"Ingestion run {summary.RunId} finished with status {summary.Status}: " +
                $"fetched {summary.PapersFetched}, added {summary.PapersAdded}, updated {summary.PapersUpdated}.");

            if (summary.Error is not null)
            {
                Console.Error.WriteLine($"Errors: {summary.Error}");
            }

            return summary.Status == IngestionStatus.Completed ? ExitOk : ExitRunFailed;
        }
        catch (IngestionAlreadyRunningException)
        {
            Console.Error.WriteLine(
                "Another ingestion run is already in progress (lease held). Try again later.");
            return ExitAlreadyRunning;
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            Console.Error.WriteLine("Ingestion cancelled.");
            return ExitRunFailed;
        }
    }

    private static bool TryParseBackfillOptions(string[] args, out BackfillOverrides? overrides)
    {
        int? days = null;
        int? maxPerCategory = null;
        overrides = null;

        for (var i = 2; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--days" when i + 1 < args.Length && int.TryParse(args[i + 1], out var d) && d > 0:
                    days = d;
                    i++;
                    break;
                case "--max-per-category" when i + 1 < args.Length && int.TryParse(args[i + 1], out var m) && m > 0:
                    maxPerCategory = m;
                    i++;
                    break;
                default:
                    Console.Error.WriteLine($"Unknown or malformed option: {args[i]}");
                    return false;
            }
        }

        overrides = days is null && maxPerCategory is null
            ? null
            : new BackfillOverrides(days, maxPerCategory);
        return true;
    }
}
