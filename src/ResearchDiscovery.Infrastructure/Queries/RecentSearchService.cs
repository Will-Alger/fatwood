using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ResearchDiscovery.Application.Abstractions;
using ResearchDiscovery.Infrastructure.Persistence;

namespace ResearchDiscovery.Infrastructure.Queries;

/// <summary>
/// Replays logged searches from telemetry. Plan JSON is deserialized with the
/// same camelCase options the telemetry writer used, so it round-trips; result
/// slots are re-hydrated into DTOs via <see cref="IPaperQueryService"/> with the
/// caller's current state. Nothing here calls the ranker or an LLM.
/// </summary>
public class RecentSearchService(
    IDbContextFactory<AppDbContext> dbFactory,
    IPaperQueryService papers) : IRecentSearchService
{
    private static readonly JsonSerializerOptions PlanJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public async Task<IReadOnlyList<RecentSearchSummary>> ListAsync(
        long userId, int limit, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var rows = await db.SearchEvents
            .AsNoTracking()
            .Where(e => e.UserId == userId)
            .OrderByDescending(e => e.CreatedUtc)
            .Take(limit)
            .Select(e => new
            {
                e.Id,
                e.CreatedUtc,
                e.QueryText,
                e.PlanJson,
                e.TotalCandidates,
                ResultCount = e.Results.Count,
            })
            .ToListAsync(ct);

        return rows
            .Select(r => new RecentSearchSummary(
                r.Id,
                r.CreatedUtc,
                r.QueryText,
                InterpretationOf(r.PlanJson),
                r.ResultCount,
                r.TotalCandidates))
            .ToList();
    }

    public async Task<RecentSearchReplay?> ReplayAsync(
        long userId, long searchEventId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var evt = await db.SearchEvents
            .AsNoTracking()
            .Where(e => e.Id == searchEventId && e.UserId == userId)
            .Select(e => new { e.PlanJson, e.TotalCandidates })
            .SingleOrDefaultAsync(ct);

        // Not found OR not owned by the caller — same 404 either way, so a
        // caller can't probe which of the two it is.
        if (evt is null)
        {
            return null;
        }

        var plan = JsonSerializer.Deserialize<SearchPlan>(evt.PlanJson, PlanJsonOptions)
            ?? throw new InvalidOperationException(
                $"Search event {searchEventId} has an unreadable plan.");

        var slots = await db.SearchEventResults
            .AsNoTracking()
            .Where(r => r.SearchEventId == searchEventId)
            .OrderBy(r => r.Rank)
            .Select(r => new { r.PaperId, r.Score, r.IsWildcard, r.Proximity, r.Variant })
            .ToListAsync(ct);

        var dtos = await papers.GetPapersByIdsAsync(
            slots.Select(s => s.PaperId).Distinct().ToList(), userId, ct);

        // Preserve the original rank order; drop slots whose paper has since
        // left the corpus (hydration miss) rather than fail the whole replay.
        var hits = slots
            .Where(s => dtos.ContainsKey(s.PaperId))
            .Select(s => new SearchHit(
                dtos[s.PaperId], s.Score, s.IsWildcard, s.Proximity, s.Variant))
            .ToList();

        return new RecentSearchReplay(searchEventId, plan, hits, evt.TotalCandidates);
    }

    /// <summary>Pulls just the interpretation out of a stored plan for the list
    /// view; tolerant of malformed JSON (returns empty rather than throwing).</summary>
    private static string InterpretationOf(string planJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(planJson);
            return doc.RootElement.TryGetProperty("interpretation", out var el)
                ? el.GetString() ?? string.Empty
                : string.Empty;
        }
        catch (JsonException)
        {
            return string.Empty;
        }
    }
}
