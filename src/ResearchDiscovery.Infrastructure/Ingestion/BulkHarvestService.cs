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
/// Historical bulk harvest over arXiv's OAI-PMH endpoint — the sanctioned path
/// for backfilling years of metadata, far beyond what the Atom query API
/// should be asked for. Add-only (OAI carries no version number, so updating
/// an existing row could regress it), page-wise durable (every page is
/// upserted before the next fetch), and resumable (the current resumption
/// token is logged every page; a crashed run continues via --resume-token).
/// Embeddings are deliberately not triggered here: after a 300k-paper harvest
/// that is a separate long job — run the `embed` CLI to catch up.
/// </summary>
public class BulkHarvestService(
    IArxivOaiClient oaiClient,
    PaperUpserter upserter,
    IIngestionLockManager lockManager,
    IDbContextFactory<AppDbContext> dbFactory,
    IOptions<ArxivOptions> arxivOptions,
    ILogger<BulkHarvestService> logger) : IBulkHarvestService
{
    private const int ProgressLogEveryPages = 10;

    public async Task<BulkHarvestSummary> RunAsync(
        DateTimeOffset fromUtc, string? onlySet, string? resumeToken, CancellationToken ct)
    {
        var configuredCategories = arxivOptions.Value.Categories
            .ToHashSet(StringComparer.Ordinal);
        var sets = DeriveSets(configuredCategories);

        if (onlySet is not null)
        {
            if (!sets.Contains(onlySet, StringComparer.Ordinal))
            {
                throw new ArgumentException(
                    $"Set '{onlySet}' does not match any configured category " +
                    $"(derived sets: {string.Join(", ", sets)}).", nameof(onlySet));
            }

            sets = [onlySet];
        }

        var holder = $"Bulk@{Environment.MachineName}";
        await using var lease = await lockManager.TryAcquireAsync(holder, ct)
            ?? throw new IngestionAlreadyRunningException();

        // Bulk harvests are manual backfills; they share the Backfill trigger
        // so the runs table needs no new enum value.
        var run = new IngestionRun
        {
            Trigger = IngestionTrigger.Backfill,
            Status = IngestionStatus.Running,
            StartedUtc = DateTimeOffset.UtcNow,
        };

        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            db.IngestionRuns.Add(run);
            await db.SaveChangesAsync(ct);
        }

        var totals = new Counters();
        var setsProcessed = 0;
        var errors = new List<string>();

        try
        {
            var categoryIdCache = new Dictionary<string, long>(StringComparer.Ordinal);

            foreach (var set in sets)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    // An OAI resumption token is self-contained (it encodes the
                    // whole query), so it can only resume the first set of the
                    // run; the remaining sets start from the beginning.
                    var tokenForSet = setsProcessed == 0 ? resumeToken : null;
                    var result = await HarvestSetAsync(
                        set, fromUtc, tokenForSet, configuredCategories, categoryIdCache, ct);
                    totals.Add(result);
                    setsProcessed++;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Bulk harvest failed for set {Set}; continuing with the next", set);
                    errors.Add($"{set}: {ex.Message}");
                    setsProcessed++;
                }
            }

            var status = errors.Count == 0 ? IngestionStatus.Completed : IngestionStatus.Failed;
            var error = errors.Count == 0 ? null : string.Join(" | ", errors);
            await FinalizeRunAsync(run.Id, status, totals, error, ct);

            logger.LogInformation(
                "Bulk harvest run {RunId} finished: {Sets} sets, {Pages} pages, fetched {Fetched}, " +
                "added {Added}, skipped existing {SkippedExisting}, filtered {Filtered}, failed sets: {FailedCount}",
                run.Id, setsProcessed, totals.Pages, totals.Fetched,
                totals.Added, totals.SkippedExisting, totals.Filtered, errors.Count);

            return new BulkHarvestSummary(
                run.Id, status, setsProcessed, totals.Pages, totals.Fetched,
                totals.Added, totals.SkippedExisting, totals.Filtered, error);
        }
        catch (Exception ex)
        {
            await FinalizeRunAsync(run.Id, IngestionStatus.Failed, totals, ex.Message, CancellationToken.None);
            throw;
        }
    }

    /// <summary>
    /// Physics-group archives are not top-level OAI sets: per ListSets they
    /// are scoped under physics ("physics:astro-ph"), and "physics:physics"
    /// selects the physics archive proper — WITHOUT this mapping, harvesting
    /// physics.comp-ph would page the entire physics supergroup (astro-ph,
    /// cond-mat, all the hep archives…) just to filter one archive.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> PhysicsGroupSets =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["physics"] = "physics:physics",
            ["astro-ph"] = "physics:astro-ph",
            ["cond-mat"] = "physics:cond-mat",
            ["gr-qc"] = "physics:gr-qc",
            ["hep-ex"] = "physics:hep-ex",
            ["hep-lat"] = "physics:hep-lat",
            ["hep-ph"] = "physics:hep-ph",
            ["hep-th"] = "physics:hep-th",
            ["math-ph"] = "physics:math-ph",
            ["nlin"] = "physics:nlin",
            ["nucl-ex"] = "physics:nucl-ex",
            ["nucl-th"] = "physics:nucl-th",
            ["quant-ph"] = "physics:quant-ph",
        };

    /// <summary>
    /// OAI sets from category codes: the archive prefix before the dot
    /// ("cs.LG" → "cs", "q-fin.CP" → "q-fin"), with physics-group archives
    /// mapped to their scoped set names; distinct, ordered.
    /// </summary>
    public static IReadOnlyList<string> DeriveSets(IEnumerable<string> categoryCodes) =>
        categoryCodes
            .Select(code =>
            {
                var dot = code.IndexOf('.');
                var archive = dot < 0 ? code : code[..dot];
                return PhysicsGroupSets.GetValueOrDefault(archive, archive);
            })
            .Where(set => set.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

    private async Task<Counters> HarvestSetAsync(
        string set,
        DateTimeOffset fromUtc,
        string? resumeToken,
        IReadOnlySet<string> configuredCategories,
        Dictionary<string, long> categoryIdCache,
        CancellationToken ct)
    {
        logger.LogInformation(
            "Bulk harvesting set {Set} from {From:yyyy-MM-dd}{Resumed}",
            set, fromUtc, resumeToken is null ? string.Empty : " (resumed from token)");

        var counters = new Counters();
        var token = resumeToken;
        int? completeListSize = null;

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            var xml = token is null
                ? await oaiClient.ListRecordsAsync(set, fromUtc, ct)
                : await oaiClient.ResumeAsync(token, ct);
            var page = ArxivOaiParser.Parse(xml);

            counters.Pages++;
            counters.Fetched += page.Entries.Count;
            completeListSize = page.CompleteListSize ?? completeListSize;

            // OAI's from= filters on datestamp (last-touched), so an old paper
            // revised yesterday shows up in a recent window, and a whole-archive
            // set spans far more categories than we track. Keep only entries
            // that are configured AND actually published in the window.
            var kept = page.Entries
                .Where(e => e.Published >= fromUtc &&
                            e.Categories.Any(configuredCategories.Contains))
                .ToList();
            counters.Filtered += page.Entries.Count - kept.Count;

            var result = await upserter.UpsertPageAsync(kept, categoryIdCache, ct, addOnly: true);
            counters.Added += result.Added;
            counters.SkippedExisting += kept.Count - result.Added;

            token = page.ResumptionToken;
            if (token is null)
            {
                break;
            }

            // The token is the resume point for a crashed run — always log it.
            logger.LogInformation(
                "Set {Set} page {Page} done; resumption token: {ResumptionToken}",
                set, counters.Pages, token);

            if (counters.Pages % ProgressLogEveryPages == 0)
            {
                object remaining = completeListSize is { } size
                    ? Math.Max(size - counters.Fetched, 0)
                    : "?";
                logger.LogInformation(
                    "Set {Set} progress: {Pages} pages, fetched {Fetched}, added {Added}, ~{Remaining} remaining",
                    set, counters.Pages, counters.Fetched, counters.Added, remaining);
            }
        }

        logger.LogInformation(
            "Bulk harvested set {Set}: {Pages} pages, fetched {Fetched}, added {Added}, " +
            "skipped existing {SkippedExisting}, filtered {Filtered}",
            set, counters.Pages, counters.Fetched, counters.Added,
            counters.SkippedExisting, counters.Filtered);

        return counters;
    }

    private async Task FinalizeRunAsync(
        long runId, IngestionStatus status, Counters totals, string? error, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var run = await db.IngestionRuns.SingleAsync(r => r.Id == runId, ct);
        run.Status = status;
        run.CompletedUtc = DateTimeOffset.UtcNow;
        run.PapersFetched = totals.Fetched;
        run.PapersAdded = totals.Added;
        run.PapersUpdated = 0; // add-only by design
        run.Error = error is { Length: > 2000 } ? error[..2000] : error;
        await db.SaveChangesAsync(ct);
    }

    private sealed class Counters
    {
        public int Pages;
        public int Fetched;
        public int Added;
        public int SkippedExisting;
        public int Filtered;

        public void Add(Counters other)
        {
            Pages += other.Pages;
            Fetched += other.Fetched;
            Added += other.Added;
            SkippedExisting += other.SkippedExisting;
            Filtered += other.Filtered;
        }
    }
}
