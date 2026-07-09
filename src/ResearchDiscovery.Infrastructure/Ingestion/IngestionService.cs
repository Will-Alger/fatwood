using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ResearchDiscovery.Application.Abstractions;
using ResearchDiscovery.Application.Options;
using ResearchDiscovery.Domain.Entities;
using ResearchDiscovery.Infrastructure.Arxiv;
using ResearchDiscovery.Infrastructure.Persistence;

namespace ResearchDiscovery.Infrastructure.Ingestion;

/// <summary>
/// Orchestrates ingestion across all configured categories. Ingestion is
/// deliberately broad (every configured category), idempotent (upsert on
/// arXiv ID), and bounded (window + per-category cap). A failed category is
/// logged and skipped so one bad category cannot sink the whole run.
/// </summary>
public class IngestionService(
    IArxivClient arxivClient,
    PaperUpserter upserter,
    IIngestionLockManager lockManager,
    IDbContextFactory<AppDbContext> dbFactory,
    IPaperEmbeddingService embeddingService,
    IOptions<ArxivOptions> arxivOptions,
    IOptions<IngestionOptions> ingestionOptions,
    ILogger<IngestionService> logger) : IIngestionService
{
    public Task<IngestionRunSummary> RunBackfillAsync(BackfillOverrides? overrides, CancellationToken ct) =>
        RunAsync(IngestionTrigger.Backfill, overrides, ct);

    public Task<IngestionRunSummary> RunDeltaAsync(CancellationToken ct) =>
        RunAsync(IngestionTrigger.Delta, null, ct);

    private async Task<IngestionRunSummary> RunAsync(
        IngestionTrigger trigger, BackfillOverrides? overrides, CancellationToken ct)
    {
        var holder = $"{trigger}@{Environment.MachineName}";
        await using var lease = await lockManager.TryAcquireAsync(holder, ct)
            ?? throw new IngestionAlreadyRunningException();

        var run = new IngestionRun
        {
            Trigger = trigger,
            Status = IngestionStatus.Running,
            StartedUtc = DateTimeOffset.UtcNow,
        };

        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            db.IngestionRuns.Add(run);
            await db.SaveChangesAsync(ct);
        }

        var fetched = 0;
        var added = 0;
        var updated = 0;
        var errors = new List<string>();

        try
        {
            var categoryIdCache = new Dictionary<string, long>(StringComparer.Ordinal);

            foreach (var code in arxivOptions.Value.Categories)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var result = await IngestCategoryAsync(code, trigger, overrides, categoryIdCache, ct);
                    fetched += result.Fetched;
                    added += result.Added;
                    updated += result.Updated;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Ingestion failed for category {Category}; continuing with the next", code);
                    errors.Add($"{code}: {ex.Message}");
                }
            }

            var status = errors.Count == 0 ? IngestionStatus.Completed : IngestionStatus.Failed;
            var error = errors.Count == 0 ? null : string.Join(" | ", errors);
            await FinalizeRunAsync(run.Id, status, fetched, added, updated, error, ct);

            logger.LogInformation(
                "Ingestion run {RunId} ({Trigger}) finished: fetched {Fetched}, added {Added}, updated {Updated}, failed categories: {FailedCount}",
                run.Id, trigger, fetched, added, updated, errors.Count);

            // Embed newly ingested papers so search stays current. Embedding
            // failures never fail the ingestion run — the `embed` CLI can
            // always catch up later.
            if (added > 0 || updated > 0)
            {
                try
                {
                    await embeddingService.EmbedMissingAsync(ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Post-ingestion embedding failed; run `embed` to catch up");
                }
            }

            return new IngestionRunSummary(run.Id, status, fetched, added, updated, error);
        }
        catch (Exception ex)
        {
            await FinalizeRunAsync(run.Id, IngestionStatus.Failed, fetched, added, updated, ex.Message, CancellationToken.None);
            throw;
        }
    }

    private async Task<(int Fetched, int Added, int Updated)> IngestCategoryAsync(
        string categoryCode,
        IngestionTrigger trigger,
        BackfillOverrides? overrides,
        Dictionary<string, long> categoryIdCache,
        CancellationToken ct)
    {
        var backfill = ingestionOptions.Value.Backfill;
        var windowDays = overrides?.WindowDays ?? backfill.WindowDays;
        var maxPapers = overrides?.MaxPapersPerCategory ?? backfill.MaxPapersPerCategory;
        var now = DateTimeOffset.UtcNow;

        var categoryId = await EnsureCategoryAsync(categoryCode, categoryIdCache, ct);

        var windowStart = now.AddDays(-windowDays);
        var from = trigger == IngestionTrigger.Delta
            ? await GetHighWaterMarkAsync(categoryId, ct) ?? windowStart
            : windowStart;

        // arXiv's submittedDate filter has minute granularity. Truncating the
        // inclusive lower bound re-fetches the boundary minute; the idempotent
        // upsert makes that harmless.
        from = TruncateToMinute(from);

        logger.LogInformation(
            "Ingesting {Category} from {From:u} to {To:u} (max {MaxPapers})",
            categoryCode, from, now, maxPapers);

        var start = 0;
        var fetched = 0;
        var added = 0;
        var updated = 0;
        DateTimeOffset? maxPublished = null;

        while (fetched < maxPapers)
        {
            var pageSize = Math.Min(arxivOptions.Value.PageSize, maxPapers - fetched);
            var page = await arxivClient.QueryAsync(
                new ArxivQuery(categoryCode, from, now, start, pageSize), ct);

            if (page.Entries.Count == 0)
            {
                break;
            }

            var result = await upserter.UpsertPageAsync(page.Entries, categoryIdCache, ct);
            fetched += page.Entries.Count;
            added += result.Added;
            updated += result.Updated;

            var pageMax = page.Entries.Max(e => e.Published);
            if (maxPublished is null || pageMax > maxPublished)
            {
                maxPublished = pageMax;
            }

            start += page.Entries.Count;
            if (start >= page.TotalResults)
            {
                break;
            }
        }

        await UpdateIngestionStateAsync(categoryId, maxPublished, ct);

        logger.LogInformation(
            "Ingested {Category}: fetched {Fetched}, added {Added}, updated {Updated}",
            categoryCode, fetched, added, updated);

        return (fetched, added, updated);
    }

    private async Task<long> EnsureCategoryAsync(
        string categoryCode, Dictionary<string, long> categoryIdCache, CancellationToken ct)
    {
        if (categoryIdCache.TryGetValue(categoryCode, out var cached))
        {
            return cached;
        }

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var category = await db.Categories.SingleOrDefaultAsync(c => c.Code == categoryCode, ct);
        if (category is null)
        {
            category = new Category
            {
                Code = categoryCode,
                Name = ArxivCategoryNames.DisplayNameFor(categoryCode),
            };
            db.Categories.Add(category);
            await db.SaveChangesAsync(ct);
        }

        categoryIdCache[categoryCode] = category.Id;
        return category.Id;
    }

    private async Task<DateTimeOffset?> GetHighWaterMarkAsync(long categoryId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var state = await db.CategoryIngestionStates
            .AsNoTracking()
            .SingleOrDefaultAsync(s => s.CategoryId == categoryId, ct);
        return state?.HighWaterMarkUtc;
    }

    private async Task UpdateIngestionStateAsync(
        long categoryId, DateTimeOffset? maxPublished, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var state = await db.CategoryIngestionStates
            .SingleOrDefaultAsync(s => s.CategoryId == categoryId, ct);

        if (state is null)
        {
            state = new CategoryIngestionState { CategoryId = categoryId };
            db.CategoryIngestionStates.Add(state);
        }

        if (maxPublished is not null &&
            (state.HighWaterMarkUtc is null || maxPublished > state.HighWaterMarkUtc))
        {
            state.HighWaterMarkUtc = maxPublished;
        }

        state.LastCompletedRunUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    private async Task FinalizeRunAsync(
        long runId, IngestionStatus status, int fetched, int added, int updated,
        string? error, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var run = await db.IngestionRuns.SingleAsync(r => r.Id == runId, ct);
        run.Status = status;
        run.CompletedUtc = DateTimeOffset.UtcNow;
        run.PapersFetched = fetched;
        run.PapersAdded = added;
        run.PapersUpdated = updated;
        run.Error = error is { Length: > 2000 } ? error[..2000] : error;
        await db.SaveChangesAsync(ct);
    }

    private static DateTimeOffset TruncateToMinute(DateTimeOffset value) =>
        value.AddTicks(-(value.Ticks % TimeSpan.TicksPerMinute));
}
