using ResearchDiscovery.Application.Eval;
using ResearchDiscovery.Infrastructure.DependencyInjection;
using ResearchDiscovery.Infrastructure.Eval;
using ResearchDiscovery.Infrastructure.Persistence;

namespace ResearchDiscovery.Api.Cli;

/// <summary>
/// Offline search-quality harness CLI. `search` is token-free and is the
/// command every ranking change must run; `compile` and `judge` spend LLM
/// tokens to (re)build the versioned ground-truth artifacts in eval/.
/// </summary>
public static class EvalCommandRunner
{
    private const int ExitOk = 0;
    private const int ExitRunFailed = 1;
    private const int ExitUsage = 64;

    private const string DefaultQueriesPath = "eval/queries.json";
    private const string DefaultJudgmentsPath = "eval/judgments.json";

    public static async Task<int> RunAsync(string[] args)
    {
        if (!TryParseArguments(args, out var verb, out var queriesPath, out var judgmentsPath,
                out var pool, out var randomSample))
        {
            Console.Error.WriteLine(
                "Usage: eval compile [--queries <path>]\n" +
                "       eval judge   [--queries <path>] [--judgments <path>] [--pool <N>] [--random <N>]\n" +
                "       eval search  [--queries <path>] [--judgments <path>]\n" +
                "       eval bias\n" +
                "       eval adopt   [--queries <path>]\n" +
                "       eval tune    [--queries <path>] [--judgments <path>]");
            return ExitUsage;
        }

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

        await using var scope = host.Services.CreateAsyncScope();
        var runner = scope.ServiceProvider.GetRequiredService<EvalRunner>();

        try
        {
            switch (verb)
            {
                case "compile":
                    var compiled = await runner.CompileAsync(queriesPath, cts.Token);
                    Console.WriteLine($"Compiled {compiled} missing plan(s) into {queriesPath}.");
                    return ExitOk;

                case "judge":
                    var judged = await runner.JudgeAsync(
                        queriesPath, judgmentsPath, pool, randomSample, cts.Token);
                    Console.WriteLine($"Added {judged} new judgment(s) to {judgmentsPath}.");
                    return ExitOk;

                case "search":
                    var report = await runner.ScoreAsync(queriesPath, judgmentsPath, cts.Token);
                    PrintReport(report);
                    return report.Queries.Count > 0 ? ExitOk : ExitRunFailed;

                case "bias":
                    var analyzer = scope.ServiceProvider.GetRequiredService<TelemetryAnalyzer>();
                    Console.WriteLine(await analyzer.BiasReportAsync(cts.Token));
                    return ExitOk;

                case "adopt":
                    var adopter = scope.ServiceProvider.GetRequiredService<TelemetryAnalyzer>();
                    var adopted = await adopter.AdoptAsync(queriesPath, cts.Token);
                    Console.WriteLine(adopted > 0
                        ? $"Adopted {adopted} real quer(ies) into {queriesPath}. " +
                          "Run `eval judge` next to grade their pools."
                        : "No new real queries to adopt (searches must carry prose query text).");
                    return ExitOk;

                case "tune":
                    var tuned = await runner.TuneAsync(queriesPath, judgmentsPath, cts.Token);
                    PrintTuneResults(tuned);
                    return tuned.Count > 0 ? ExitOk : ExitRunFailed;

                default:
                    return ExitUsage;
            }
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            Console.Error.WriteLine("Eval cancelled; artifacts saved so far were kept.");
            return ExitRunFailed;
        }
        catch (Exception ex) when (ex is InvalidOperationException or FileNotFoundException)
        {
            Console.Error.WriteLine(ex.Message);
            return ExitRunFailed;
        }
    }

    private static void PrintReport(EvalReport report)
    {
        if (report.Queries.Count == 0)
        {
            Console.WriteLine("No scorable queries: need compiled plans AND judgments. " +
                              "Run `eval compile` then `eval judge` first.");
            return;
        }

        Console.WriteLine();
        Console.WriteLine($"{"query",-28} {"nDCG@10",8} {"Recall@50",10} {"MRR",6}  {"judged@10",9} {"judged",7} {"relevant",8}");
        Console.WriteLine(new string('-', 82));

        foreach (var q in report.Queries)
        {
            Console.WriteLine(
                $"{q.QueryId,-28} {Fmt(q.Ndcg10),8} {Fmt(q.Recall50),10} {Fmt(q.ReciprocalRank),6}  " +
                $"{q.JudgedInTop10,7}/10 {q.JudgedTotal,7} {q.RelevantTotal,8}");
        }

        Console.WriteLine(new string('-', 82));
        Console.WriteLine(
            $"{"MEAN",-28} {Fmt(report.MeanNdcg10),8} {Fmt(report.MeanRecall50),10} {Fmt(report.MeanReciprocalRank),6}");
        Console.WriteLine();
        Console.WriteLine("judged@10 < 10 means unjudged papers reached the top 10 (scored as grade 0);");
        Console.WriteLine("re-run `eval judge` to grade the current ranker's head before trusting deltas.");
    }

    private static void PrintTuneResults(IReadOnlyList<EvalRunner.TuneResult> results)
    {
        Console.WriteLine();
        Console.WriteLine($"{"recency",8} {"halfLife",9} {"codeBonus",10} {"nDCG@10",8} {"Recall@50",10} {"MRR",6}");
        Console.WriteLine(new string('-', 58));
        foreach (var r in results)
        {
            var marker = r.Weights.IsPureSimilarity ? "  <- current default" : string.Empty;
            Console.WriteLine(
                $"{r.Weights.RecencyWeight,8:0.00} {r.Weights.RecencyHalfLifeDays,8}d {r.Weights.CodeBonus,10:0.00} " +
                $"{Fmt(r.MeanNdcg10),8} {Fmt(r.MeanRecall50),10} {Fmt(r.MeanMrr),6}{marker}");
        }

        Console.WriteLine(new string('-', 58));
        var best = results[0];
        Console.WriteLine(best.Weights.IsPureSimilarity
            ? "Pure similarity is already the best measured blend — change nothing."
            : $"Best blend: Ranking__RecencyWeight={best.Weights.RecencyWeight:0.00} " +
              $"Ranking__RecencyHalfLifeDays={best.Weights.RecencyHalfLifeDays} " +
              $"Ranking__CodeBonus={best.Weights.CodeBonus:0.00} — apply via configuration " +
              "if the delta is worth it; nothing is applied automatically.");
    }

    private static string Fmt(double? value) => value?.ToString("0.000") ?? "n/a";

    private static bool TryParseArguments(
        string[] args,
        out string verb,
        out string queriesPath,
        out string judgmentsPath,
        out int pool,
        out int randomSample)
    {
        verb = string.Empty;
        queriesPath = DefaultQueriesPath;
        judgmentsPath = DefaultJudgmentsPath;
        pool = 30;
        randomSample = 10;

        if (args.Length < 2)
        {
            return false;
        }

        verb = args[1].ToLowerInvariant();
        if (verb is not ("compile" or "judge" or "search" or "bias" or "adopt" or "tune"))
        {
            return false;
        }

        for (var i = 2; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--queries" when i + 1 < args.Length:
                    queriesPath = args[++i];
                    break;
                case "--judgments" when i + 1 < args.Length:
                    judgmentsPath = args[++i];
                    break;
                case "--pool" when i + 1 < args.Length && int.TryParse(args[i + 1], out var p) && p > 0:
                    pool = p;
                    i++;
                    break;
                case "--random" when i + 1 < args.Length && int.TryParse(args[i + 1], out var r) && r >= 0:
                    randomSample = r;
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
