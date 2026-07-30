using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ResearchDiscovery.Application.Abstractions;
using ResearchDiscovery.Application.Options;
using ResearchDiscovery.Infrastructure.Persistence;

namespace ResearchDiscovery.Infrastructure.Search;

/// <summary>
/// In-memory candidate sets for the stage-0 search filters. A category
/// search previously SELECTed every matching paper id per request (~700 ms
/// for cs.LG at the 916k corpus); this caches per-category sorted id arrays
/// plus per-paper published ticks and has-code flags, and composes them with
/// exactly the SQL path's semantics (ANY-of categories, sub-day date cutoff,
/// CodeUrl null check). Same lazy-load / volatile-swap / Invalidate
/// lifecycle as the packed search indexes. Contents change only on ingest
/// (Papers, PaperCategories, CodeUrl are written nowhere else), so
/// invalidation hooks live in the ingestion services, with the TTL in
/// SearchOptions as a backstop for any future writer.
/// </summary>
public class CandidateSetCache(
    IDbContextFactory<AppDbContext> dbFactory,
    IOptions<SearchOptions> options,
    ILogger<CandidateSetCache> logger) : ICandidateSetCache
{
    private readonly SemaphoreSlim _loadLock = new(1, 1);
    private volatile Payload? _data;

    public async Task<IReadOnlySet<long>> GetAsync(
        IReadOnlyList<string> categories,
        bool requireNoCode,
        DateTimeOffset? publishedAfter,
        CancellationToken ct)
    {
        var data = await GetDataAsync(ct);

        HashSet<long> result;
        if (categories.Count > 0)
        {
            result = [];
            foreach (var code in categories)
            {
                if (data.ByCategory.TryGetValue(code, out var ids))
                {
                    foreach (var id in ids)
                    {
                        result.Add(id);
                    }
                }
            }
        }
        else
        {
            // Only the RequireNoCode-without-categories case lands here.
            result = [.. data.AllIds];
        }

        if (requireNoCode)
        {
            foreach (var id in data.HasCodeIds)
            {
                result.Remove(id);
            }
        }

        if (publishedAfter is { } cutoff)
        {
            var cutoffTicks = cutoff.UtcTicks;
            result.RemoveWhere(id =>
            {
                var i = Array.BinarySearch(data.AllIds, id);
                return i < 0 || data.PublishedTicks[i] < cutoffTicks;
            });
        }

        return result;
    }

    public void Invalidate() => _data = null;

    private async Task<Payload> GetDataAsync(CancellationToken ct)
    {
        var cached = _data;
        if (IsFresh(cached))
        {
            return cached!;
        }

        await _loadLock.WaitAsync(ct);
        try
        {
            cached = _data;
            if (IsFresh(cached))
            {
                return cached!;
            }

            await using var db = await dbFactory.CreateDbContextAsync(ct);

            var papers = await db.Papers
                .AsNoTracking()
                .OrderBy(p => p.Id)
                .Select(p => new { p.Id, p.PublishedUtc, HasCode = p.CodeUrl != null })
                .ToListAsync(ct);

            var allIds = new long[papers.Count];
            var publishedTicks = new long[papers.Count];
            var hasCode = new List<long>();
            for (var i = 0; i < papers.Count; i++)
            {
                allIds[i] = papers[i].Id;
                publishedTicks[i] = papers[i].PublishedUtc.UtcTicks;
                if (papers[i].HasCode)
                {
                    hasCode.Add(papers[i].Id);
                }
            }

            var pairs = await db.PaperCategories
                .AsNoTracking()
                .Select(pc => new { pc.Category.Code, pc.PaperId })
                .ToListAsync(ct);
            var byCategory = pairs
                .GroupBy(p => p.Code, StringComparer.Ordinal)
                .ToDictionary(
                    g => g.Key,
                    g => g.Select(x => x.PaperId).Order().ToArray(),
                    StringComparer.Ordinal);

            var payload = new Payload
            {
                ByCategory = byCategory,
                AllIds = allIds,
                PublishedTicks = publishedTicks,
                HasCodeIds = [.. hasCode],
                LoadedUtc = DateTimeOffset.UtcNow,
            };
            _data = payload;

            logger.LogInformation(
                "Candidate-set cache loaded: {Papers} papers, {Categories} categories, {HasCode} with code",
                allIds.Length, byCategory.Count, hasCode.Count);
            return payload;
        }
        finally
        {
            _loadLock.Release();
        }
    }

    private bool IsFresh(Payload? payload)
    {
        if (payload is null)
        {
            return false;
        }

        var ttlMinutes = options.Value.CandidateCacheTtlMinutes;
        return ttlMinutes <= 0
            || DateTimeOffset.UtcNow - payload.LoadedUtc < TimeSpan.FromMinutes(ttlMinutes);
    }

    private sealed class Payload
    {
        public required Dictionary<string, long[]> ByCategory { get; init; }

        public required long[] AllIds { get; init; }

        public required long[] PublishedTicks { get; init; }

        public required long[] HasCodeIds { get; init; }

        public required DateTimeOffset LoadedUtc { get; init; }
    }
}
