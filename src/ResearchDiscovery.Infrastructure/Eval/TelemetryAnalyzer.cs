using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ResearchDiscovery.Application.Abstractions;
using ResearchDiscovery.Application.Eval;
using ResearchDiscovery.Infrastructure.Persistence;
using ResearchDiscovery.Infrastructure.Profile;
using ResearchDiscovery.Infrastructure.Search;

namespace ResearchDiscovery.Infrastructure.Eval;

/// <summary>
/// Reads the product telemetry (SearchEvents / InteractionEvents) offline:
///
///   bias  — systematic skews in what the ranker shows vs. what the filtered
///           candidates look like, plus how the user's attention distributes.
///           A REPORT for a human; nothing here retunes anything (auto-tuning
///           from the ranker's own outputs is a feedback loop).
///   adopt — promotes real logged queries into eval/queries.json so the
///           harness measures the query distribution that actually happens.
/// </summary>
public class TelemetryAnalyzer(
    IDbContextFactory<AppDbContext> dbFactory,
    ProfileService profileService,
    ILogger<TelemetryAnalyzer> logger)
{
    private const int HeadSize = 10;

    private static readonly JsonSerializerOptions PlanJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public async Task<string> BiasReportAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var events = await db.SearchEvents
            .AsNoTracking()
            .OrderBy(e => e.CreatedUtc)
            .Select(e => new { e.Id, e.CreatedUtc, e.PlanJson, e.TotalCandidates })
            .ToListAsync(ct);

        if (events.Count == 0)
        {
            return "No searches logged yet — run some searches in the app first.";
        }

        var head = await db.SearchEventResults
            .AsNoTracking()
            .Where(r => r.Rank <= HeadSize)
            .Select(r => new
            {
                r.SearchEventId,
                r.Rank,
                r.IsWildcard,
                r.Proximity,
                r.PaperId,
                Category = r.Paper.PrimaryCategory.Code,
                r.Paper.PublishedUtc,
                AbstractLength = r.Paper.Abstract.Length,
            })
            .ToListAsync(ct);

        var allResults = await db.SearchEventResults
            .AsNoTracking()
            .Select(r => new { r.SearchEventId, r.PaperId, r.IsWildcard })
            .ToListAsync(ct);

        var interactions = await db.InteractionEvents
            .AsNoTracking()
            .Select(i => new { i.Type, i.SearchEventId, i.Rank, i.PaperId })
            .ToListAsync(ct);

        // Candidate baselines per distinct plan (searches repeat plans often).
        var candidateStats = new Dictionary<string, (Dictionary<string, int> ByCategory, int Total, double MeanAgeDays, double MeanAbstractLength)>();
        var now = DateTimeOffset.UtcNow;
        foreach (var planJson in events.Select(e => e.PlanJson).Distinct())
        {
            var plan = JsonSerializer.Deserialize<SearchPlan>(planJson, PlanJsonOptions);
            if (plan is null)
            {
                continue;
            }

            var rows = await SearchService
                .FilterCandidates(db.Papers.AsNoTracking(), plan)
                .Select(p => new
                {
                    Category = p.PrimaryCategory.Code,
                    p.PublishedUtc,
                    Len = p.Abstract.Length,
                })
                .ToListAsync(ct);

            if (rows.Count == 0)
            {
                continue;
            }

            candidateStats[planJson] = (
                rows.GroupBy(r => r.Category).ToDictionary(g => g.Key, g => g.Count()),
                rows.Count,
                rows.Average(r => (now - r.PublishedUtc).TotalDays),
                rows.Average(r => (double)r.Len));
        }

        var report = new StringBuilder();
        var span = $"{events[0].CreatedUtc:yyyy-MM-dd} → {events[^1].CreatedUtc:yyyy-MM-dd}";
        report.AppendLine($"Bias report over {events.Count} logged search(es), {span}; " +
                          $"{interactions.Count} interaction(s).");
        report.AppendLine();

        // ---- Category skew: shown head vs candidate pool -------------------
        var shownByCategory = head
            .GroupBy(h => h.Category)
            .ToDictionary(g => g.Key, g => (double)g.Count() / head.Count);

        var baselineByCategory = new Dictionary<string, double>();
        var weightedEvents = events.Where(e => candidateStats.ContainsKey(e.PlanJson)).ToList();
        foreach (var e in weightedEvents)
        {
            var (byCat, total, _, _) = candidateStats[e.PlanJson];
            foreach (var (cat, count) in byCat)
            {
                baselineByCategory[cat] = baselineByCategory.GetValueOrDefault(cat)
                    + (double)count / total / weightedEvents.Count;
            }
        }

        report.AppendLine($"Category share, shown top-{HeadSize} vs candidate pool (Δ > 0 = over-shown):");
        var deltas = shownByCategory.Keys.Union(baselineByCategory.Keys)
            .Select(cat => (Cat: cat,
                Shown: shownByCategory.GetValueOrDefault(cat),
                Base: baselineByCategory.GetValueOrDefault(cat)))
            .OrderByDescending(x => Math.Abs(x.Shown - x.Base))
            .Take(8);
        foreach (var (cat, shown, baseline) in deltas)
        {
            var delta = shown - baseline;
            report.AppendLine(
                $"  {cat,-14} shown {shown,6:P1}   pool {baseline,6:P1}   Δ {(delta >= 0 ? "+" : "")}{delta:P1}");
        }

        // ---- Recency & length skew ----------------------------------------
        if (weightedEvents.Count > 0)
        {
            var shownMeanAge = head.Average(h => (now - h.PublishedUtc).TotalDays);
            var baseMeanAge = weightedEvents.Average(e => candidateStats[e.PlanJson].MeanAgeDays);
            var shownMeanLen = head.Average(h => (double)h.AbstractLength);
            var baseMeanLen = weightedEvents.Average(e => candidateStats[e.PlanJson].MeanAbstractLength);

            report.AppendLine();
            report.AppendLine($"Recency: shown mean age {shownMeanAge,6:F1}d vs pool {baseMeanAge,6:F1}d " +
                              $"({(shownMeanAge < baseMeanAge ? "newer-leaning" : "older-leaning")})");
            report.AppendLine($"Length:  shown mean abstract {shownMeanLen,6:F0} chars vs pool {baseMeanLen,6:F0} " +
                              $"({(shownMeanLen < baseMeanLen ? "shorter-leaning" : "longer-leaning")})");
        }

        // ---- Comfort-zone mix ----------------------------------------------
        var annotated = head.Where(h => !h.IsWildcard).ToList();
        if (annotated.Count > 0)
        {
            var close = annotated.Count(h => h.Proximity == "close");
            var stretch = annotated.Count(h => h.Proximity == "stretch");
            report.AppendLine();
            report.AppendLine($"Comfort zone (non-wildcard top-{HeadSize}): " +
                              $"close {close * 100 / annotated.Count}%, " +
                              $"stretch {stretch * 100 / annotated.Count}%, " +
                              $"neutral {(annotated.Count - close - stretch) * 100 / annotated.Count}% " +
                              "— watch close% creeping up over time (exploration eroding).");
        }

        // ---- Wildcard yield --------------------------------------------------
        var wildcardKeys = allResults.Where(r => r.IsWildcard)
            .Select(r => (r.SearchEventId, r.PaperId)).ToHashSet();
        var contextual = interactions.Where(i => i.SearchEventId is not null).ToList();
        var wildcardHits = contextual.Count(i =>
            wildcardKeys.Contains((i.SearchEventId!.Value, i.PaperId)));
        var wildcardsShown = allResults.Count(r => r.IsWildcard);

        report.AppendLine();
        report.AppendLine($"Wildcards: {wildcardsShown} shown, {wildcardHits} interacted with " +
                          $"({(wildcardsShown > 0 ? (double)wildcardHits / wildcardsShown : 0):P1} yield; " +
                          $"non-wildcard yield {(allResults.Count - wildcardsShown > 0 ? (double)(contextual.Count - wildcardHits) / (allResults.Count - wildcardsShown) : 0):P1}) " +
                          "— zero wildcard engagement over many searches means the slots need rethinking.");

        // ---- Position bias in the user's own attention ----------------------
        if (contextual.Count > 0)
        {
            var top3 = contextual.Count(i => i.Rank is >= 1 and <= 3);
            var mid = contextual.Count(i => i.Rank is >= 4 and <= 10);
            var tail = contextual.Count(i => i.Rank is > 10);
            var unknown = contextual.Count(i => i.Rank is null);
            report.AppendLine();
            report.AppendLine($"Interaction ranks: 1-3: {top3}, 4-10: {mid}, 11+: {tail}, unresolved: {unknown} " +
                              $"(+{interactions.Count - contextual.Count} without search context).");
            if (contextual.Count >= 10 && top3 >= contextual.Count * 9 / 10)
            {
                report.AppendLine("  ⚠ >90% of interactions land in the top 3 — labels mostly measure where you look, " +
                                  "not what is good. Discount position when using these as training labels.");
            }
        }

        report.AppendLine();
        report.AppendLine("This is a report, not an actuator: any weight change it motivates goes through " +
                          "`eval tune` + `eval search` and is applied by a human in configuration.");
        return report.ToString();
    }

    /// <summary>
    /// Promotes logged real queries (those with prose) into the eval query
    /// set. Idempotent: matching is by normalized query text; the newest
    /// logged plan wins for a repeated query.
    /// </summary>
    public async Task<int> AdoptAsync(string queriesPath, CancellationToken ct)
    {
        var set = EvalFileStore.LoadQueries(queriesPath);
        var existingTexts = set.Queries
            .Select(q => Normalize(q.Query))
            .ToHashSet(StringComparer.Ordinal);
        var existingIds = set.Queries.Select(q => q.Id).ToHashSet(StringComparer.Ordinal);

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var logged = await db.SearchEvents
            .AsNoTracking()
            .Where(e => e.QueryText != null)
            .OrderByDescending(e => e.CreatedUtc)
            .Select(e => new { e.QueryText, e.PlanJson })
            .ToListAsync(ct);

        var profile = await profileService.GetAsync(ct);
        var persona = ProfileService.Describe(profile)
            ?? "(no saved profile at adoption time)";

        var queries = set.Queries.ToList();
        var adopted = 0;
        foreach (var group in logged.GroupBy(e => Normalize(e.QueryText!)))
        {
            if (existingTexts.Contains(group.Key))
            {
                continue;
            }

            var newest = group.First();
            var plan = JsonSerializer.Deserialize<SearchPlan>(newest.PlanJson, PlanJsonOptions);
            if (plan is null)
            {
                continue;
            }

            var id = Slug(newest.QueryText!, existingIds);
            existingIds.Add(id);
            existingTexts.Add(group.Key);
            queries.Add(new EvalQuery(id, persona, newest.QueryText!.Trim(), plan));
            adopted++;
            logger.LogInformation("Adopted real query into eval set as {Id}: {Query}", id, newest.QueryText);
        }

        if (adopted > 0)
        {
            EvalFileStore.SaveQueries(queriesPath, set with { Queries = queries });
        }

        return adopted;
    }

    internal static string Normalize(string text) =>
        string.Join(' ', text.ToLowerInvariant().Split(
            [' ', '\t', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries));

    internal static string Slug(string query, IReadOnlySet<string> taken)
    {
        var words = new string(query.ToLowerInvariant()
                .Select(c => char.IsLetterOrDigit(c) ? c : ' ')
                .ToArray())
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Take(6);

        var slug = $"adopted-{string.Join('-', words)}";
        if (slug == "adopted-")
        {
            slug = "adopted-query";
        }

        var candidate = slug;
        for (var i = 2; taken.Contains(candidate); i++)
        {
            candidate = $"{slug}-{i}";
        }

        return candidate;
    }
}
