using Microsoft.Extensions.Options;
using ResearchDiscovery.Application.Abstractions;
using ResearchDiscovery.Application.Options;
using ResearchDiscovery.Infrastructure.DependencyInjection;
using ResearchDiscovery.Infrastructure.Persistence;

namespace ResearchDiscovery.Api.Cli;

/// <summary>
/// One-shot CLI analysis mode — the ops-facing manual trigger for Phase 2,
/// mirroring IngestCommandRunner. Analyzes a single category (bounded) and
/// exits; regular users never reach this.
/// </summary>
public static class AnalyzeCommandRunner
{
    private const int ExitOk = 0;
    private const int ExitRunFailed = 1;
    private const int ExitUsage = 64;

    public static async Task<int> RunAsync(string[] args)
    {
        if (!TryParseArguments(args, out var categoryCode, out var maxPapers, out var sinceDays))
        {
            Console.Error.WriteLine(
                "Usage: analyze <category-code> [--max <N>] [--since-days <N>]");
            return ExitUsage;
        }

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        // Deliberately not forwarding args, same as the ingest CLI: verbs are
        // not configuration.
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddInfrastructure(builder.Configuration);

        using var host = builder.Build();
        await DatabaseStartup.MigrateIfConfiguredAsync(host.Services, builder.Configuration, cts.Token);

        await using var scope = host.Services.CreateAsyncScope();
        var analysis = scope.ServiceProvider.GetRequiredService<IAnalysisService>();
        var options = scope.ServiceProvider.GetRequiredService<IOptions<AnalysisOptions>>().Value;

        var request = new AnalysisRequest(
            categoryCode,
            maxPapers ?? options.DefaultMaxPapers,
            sinceDays is { } d ? DateTimeOffset.UtcNow.AddDays(-d) : null);

        try
        {
            var summary = await analysis.AnalyzeAsync(request, cts.Token);

            Console.WriteLine(
                $"Analysis run for {summary.CategoryCode} finished: " +
                $"selected {summary.PapersSelected}, analyzed {summary.PapersAnalyzed}, " +
                $"declined {summary.PapersDeclined}, failed {summary.PapersFailed}.");

            return summary.PapersFailed == 0 ? ExitOk : ExitRunFailed;
        }
        catch (UnknownCategoryException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return ExitUsage;
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            Console.Error.WriteLine("Analysis cancelled; completed papers were kept.");
            return ExitRunFailed;
        }
    }

    private static bool TryParseArguments(
        string[] args, out string categoryCode, out int? maxPapers, out int? sinceDays)
    {
        categoryCode = string.Empty;
        maxPapers = null;
        sinceDays = null;

        if (args.Length < 2 || args[1].StartsWith("--", StringComparison.Ordinal))
        {
            return false;
        }

        categoryCode = args[1];

        for (var i = 2; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--max" when i + 1 < args.Length && int.TryParse(args[i + 1], out var m) && m > 0:
                    maxPapers = m;
                    i++;
                    break;
                case "--since-days" when i + 1 < args.Length && int.TryParse(args[i + 1], out var s) && s > 0:
                    sinceDays = s;
                    i++;
                    break;
                default:
                    Console.Error.WriteLine($"Unknown or malformed option: {args[i]}");
                    return false;
            }
        }

        return true;
    }
}
