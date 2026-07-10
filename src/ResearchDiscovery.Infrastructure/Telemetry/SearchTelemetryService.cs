using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ResearchDiscovery.Application.Abstractions;
using ResearchDiscovery.Domain.Entities;
using ResearchDiscovery.Infrastructure.Persistence;

namespace ResearchDiscovery.Infrastructure.Telemetry;

public class SearchTelemetryService(
    IDbContextFactory<AppDbContext> dbFactory,
    ILogger<SearchTelemetryService> logger) : ISearchTelemetry
{
    private static readonly JsonSerializerOptions PlanJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public async Task<long> LogSearchAsync(
        string? queryText, SearchPlan plan, SearchResult result, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        // Hits carry DTOs (arXiv ids); results need row ids.
        var arxivIds = result.Hits.Select(h => h.Paper.ArxivId).ToList();
        var idByArxivId = await db.Papers
            .AsNoTracking()
            .Where(p => arxivIds.Contains(p.ArxivId))
            .Select(p => new { p.Id, p.ArxivId })
            .ToDictionaryAsync(p => p.ArxivId, p => p.Id, StringComparer.Ordinal, ct);

        var searchEvent = new SearchEvent
        {
            CreatedUtc = DateTimeOffset.UtcNow,
            QueryText = string.IsNullOrWhiteSpace(queryText) ? null : queryText.Trim(),
            PlanJson = JsonSerializer.Serialize(plan, PlanJsonOptions),
            TotalCandidates = result.TotalCandidates,
            ResultLimit = result.Hits.Count,
        };

        var rank = 0;
        foreach (var hit in result.Hits)
        {
            rank++;
            if (!idByArxivId.TryGetValue(hit.Paper.ArxivId, out var paperId))
            {
                continue;
            }

            searchEvent.Results.Add(new SearchEventResult
            {
                Rank = rank,
                PaperId = paperId,
                Score = hit.MatchScore,
                IsWildcard = hit.IsWildcard,
                Proximity = hit.ExperienceProximity,
                Variant = hit.Variant,
            });
        }

        db.SearchEvents.Add(searchEvent);
        await db.SaveChangesAsync(ct);
        return searchEvent.Id;
    }

    public async Task LogInteractionAsync(
        string arxivId, InteractionType type, long? searchEventId, int? rank, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var paperId = await db.Papers
            .AsNoTracking()
            .Where(p => p.ArxivId == arxivId)
            .Select(p => (long?)p.Id)
            .SingleOrDefaultAsync(ct);

        if (paperId is null)
        {
            logger.LogWarning("Interaction on unknown paper {ArxivId} — not logged", arxivId);
            return;
        }

        if (searchEventId is not null && rank is null)
        {
            rank = await db.SearchEventResults
                .AsNoTracking()
                .Where(r => r.SearchEventId == searchEventId && r.PaperId == paperId)
                .Select(r => (int?)r.Rank)
                .FirstOrDefaultAsync(ct);
        }

        db.InteractionEvents.Add(new InteractionEvent
        {
            CreatedUtc = DateTimeOffset.UtcNow,
            PaperId = paperId.Value,
            Type = type,
            SearchEventId = searchEventId,
            Rank = rank,
        });

        await db.SaveChangesAsync(ct);
    }
}
