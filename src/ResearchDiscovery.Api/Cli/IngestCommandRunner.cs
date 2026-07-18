using System.Globalization;
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
        if (mode is not ("backfill" or "delta" or "bulk"))
        {
            Console.Error.WriteLine(
                "Usage: ingest backfill [--days <N>] [--max-per-category <N>] | ingest delta" +
                " | ingest bulk --from <YYYY-MM-DD> [--set <set>] [--resume-token <token>]");
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

        BulkOptions? bulkOptions = null;
        if (mode == "bulk")
        {
            if (!TryParseBulkOptions(args, out bulkOptions))
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

        try
        {
            if (mode == "bulk")
            {
                var bulk = scope.ServiceProvider.GetRequiredService<IBulkHarvestService>();
                var bulkSummary = await bulk.RunAsync(
                    bulkOptions!.FromUtc, bulkOptions.Set, bulkOptions.ResumeToken, cts.Token);

                Console.WriteLine(
                    $"Bulk harvest run {bulkSummary.RunId} finished with status {bulkSummary.Status}: " +
                    $"{bulkSummary.SetsProcessed} sets, {bulkSummary.Pages} pages, " +
                    $"fetched {bulkSummary.PapersFetched}, added {bulkSummary.PapersAdded}, " +
                    $"skipped existing {bulkSummary.PapersSkippedExisting}, filtered {bulkSummary.PapersFiltered}.");

                if (bulkSummary.Error is not null)
                {
                    Console.Error.WriteLine($"Errors: {bulkSummary.Error}");
                }

                return bulkSummary.Status == IngestionStatus.Completed ? ExitOk : ExitRunFailed;
            }

            var ingestion = scope.ServiceProvider.GetRequiredService<IIngestionService>();
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
        catch (ArgumentException ex) when (mode == "bulk")
        {
            // e.g. --set names an archive no configured category belongs to.
            Console.Error.WriteLine(ex.Message);
            return ExitUsage;
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

    private sealed record BulkOptions(DateTimeOffset FromUtc, string? Set, string? ResumeToken);

    private static bool TryParseBulkOptions(string[] args, out BulkOptions? options)
    {
        DateTimeOffset? from = null;
        string? set = null;
        string? resumeToken = null;
        options = null;

        for (var i = 2; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--from" when i + 1 < args.Length && DateTimeOffset.TryParseExact(
                        args[i + 1], "yyyy-MM-dd", CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var f):
                    from = f;
                    i++;
                    break;
                case "--set" when i + 1 < args.Length && !string.IsNullOrWhiteSpace(args[i + 1]):
                    set = args[i + 1];
                    i++;
                    break;
                case "--resume-token" when i + 1 < args.Length && !string.IsNullOrWhiteSpace(args[i + 1]):
                    resumeToken = args[i + 1];
                    i++;
                    break;
                default:
                    Console.Error.WriteLine($"Unknown or malformed option: {args[i]}");
                    return false;
            }
        }

        if (from is null)
        {
            Console.Error.WriteLine("ingest bulk requires --from <YYYY-MM-DD> (ISO date, UTC).");
            return false;
        }

        options = new BulkOptions(from.Value, set, resumeToken);
        return true;
    }
}
