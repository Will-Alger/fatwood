using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ResearchDiscovery.Application.Abstractions;
using ResearchDiscovery.Domain.Entities;
using ResearchDiscovery.Infrastructure.Arxiv;
using ResearchDiscovery.Infrastructure.Persistence;

namespace ResearchDiscovery.Infrastructure.Ingestion;

/// <summary>
/// Idempotent, provider-agnostic upsert of one arXiv result page. Keyed on the
/// unique ArxivId index; deliberately avoids ON CONFLICT / MERGE so nothing
/// here changes when the EF Core provider is swapped. Writers are serialized
/// by the ingestion lease; the single retry below is belt-and-braces for a
/// unique-index race, detected by re-querying rather than SQLSTATE sniffing.
/// </summary>
public class PaperUpserter(
    IDbContextFactory<AppDbContext> dbFactory,
    ILogger<PaperUpserter> logger)
{
    public sealed record UpsertResult(int Added, int Updated);

    /// <param name="categoryIdCache">Per-run cache of category code → id; appended as new codes appear.</param>
    /// <param name="addOnly">When true, papers that already exist are left
    /// untouched instead of updated. Bulk historical harvests use this: the
    /// OAI-PMH metadata format carries no version number, so letting it
    /// update an existing row would regress <c>LatestVersion</c>.</param>
    public async Task<UpsertResult> UpsertPageAsync(
        IReadOnlyList<ArxivEntry> entries,
        Dictionary<string, long> categoryIdCache,
        CancellationToken ct,
        bool addOnly = false)
    {
        if (entries.Count == 0)
        {
            return new UpsertResult(0, 0);
        }

        try
        {
            return await UpsertCoreAsync(entries, categoryIdCache, addOnly, ct);
        }
        catch (DbUpdateException ex)
        {
            logger.LogWarning(ex, "Page upsert hit a database conflict; retrying the page once");
            return await UpsertCoreAsync(entries, categoryIdCache, addOnly, ct);
        }
    }

    private async Task<UpsertResult> UpsertCoreAsync(
        IReadOnlyList<ArxivEntry> entries,
        Dictionary<string, long> categoryIdCache,
        bool addOnly,
        CancellationToken ct)
    {
        // Fresh context per page keeps the change tracker bounded (≤ pageSize).
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        await EnsureCategoriesAsync(db, entries, categoryIdCache, ct);

        var ids = entries.Select(e => e.ArxivId).Distinct(StringComparer.Ordinal).ToList();
        var existing = await db.Papers
            .Include(p => p.PaperCategories)
            .Where(p => ids.Contains(p.ArxivId))
            .ToDictionaryAsync(p => p.ArxivId, StringComparer.Ordinal, ct);

        var now = DateTimeOffset.UtcNow;
        var added = 0;
        var updated = 0;

        foreach (var entry in entries.DistinctBy(e => e.ArxivId, StringComparer.Ordinal))
        {
            if (existing.TryGetValue(entry.ArxivId, out var paper))
            {
                if (!addOnly && ApplyUpdate(db, paper, entry, categoryIdCache, now))
                {
                    updated++;
                }
            }
            else
            {
                db.Papers.Add(CreatePaper(entry, categoryIdCache, now));
                added++;
            }
        }

        await db.SaveChangesAsync(ct);
        return new UpsertResult(added, updated);
    }

    private static async Task EnsureCategoriesAsync(
        AppDbContext db,
        IReadOnlyList<ArxivEntry> entries,
        Dictionary<string, long> categoryIdCache,
        CancellationToken ct)
    {
        var missing = entries
            .SelectMany(e => e.Categories)
            .Distinct(StringComparer.Ordinal)
            .Where(code => !categoryIdCache.ContainsKey(code))
            .ToList();

        if (missing.Count == 0)
        {
            return;
        }

        var known = await db.Categories.Where(c => missing.Contains(c.Code)).ToListAsync(ct);
        foreach (var category in known)
        {
            categoryIdCache[category.Code] = category.Id;
        }

        var toCreate = missing
            .Where(code => !categoryIdCache.ContainsKey(code))
            .Select(code => new Category { Code = code, Name = ArxivCategoryNames.DisplayNameFor(code) })
            .ToList();

        if (toCreate.Count > 0)
        {
            db.Categories.AddRange(toCreate);
            await db.SaveChangesAsync(ct);
            foreach (var category in toCreate)
            {
                categoryIdCache[category.Code] = category.Id;
            }
        }
    }

    private static Paper CreatePaper(
        ArxivEntry entry,
        IReadOnlyDictionary<string, long> categoryIdCache,
        DateTimeOffset now) =>
        new()
        {
            ArxivId = entry.ArxivId,
            LatestVersion = entry.Version,
            Title = entry.Title,
            Abstract = entry.Abstract,
            Authors = string.Join("; ", entry.Authors),
            PrimaryCategoryId = categoryIdCache[entry.PrimaryCategory],
            PublishedUtc = entry.Published,
            UpdatedUtc = entry.Updated,
            AbsUrl = entry.AbsUrl,
            PdfUrl = entry.PdfUrl,
            Doi = entry.Doi,
            CodeUrl = entry.CodeUrl,
            FirstIngestedUtc = now,
            LastSeenUtc = now,
            PaperCategories = entry.Categories
                .Select(code => new PaperCategory { CategoryId = categoryIdCache[code] })
                .ToList(),
        };

    private static bool ApplyUpdate(
        AppDbContext db,
        Paper paper,
        ArxivEntry entry,
        IReadOnlyDictionary<string, long> categoryIdCache,
        DateTimeOffset now)
    {
        var authors = string.Join("; ", entry.Authors);
        var primaryCategoryId = categoryIdCache[entry.PrimaryCategory];

        var changed =
            paper.LatestVersion != entry.Version ||
            paper.Title != entry.Title ||
            paper.Abstract != entry.Abstract ||
            paper.Authors != authors ||
            paper.PrimaryCategoryId != primaryCategoryId ||
            paper.PublishedUtc != entry.Published ||
            paper.UpdatedUtc != entry.Updated ||
            paper.AbsUrl != entry.AbsUrl ||
            paper.PdfUrl != entry.PdfUrl ||
            paper.Doi != entry.Doi ||
            paper.CodeUrl != entry.CodeUrl;

        paper.LatestVersion = entry.Version;
        paper.Title = entry.Title;
        paper.Abstract = entry.Abstract;
        paper.Authors = authors;
        paper.PrimaryCategoryId = primaryCategoryId;
        paper.PublishedUtc = entry.Published;
        paper.UpdatedUtc = entry.Updated;
        paper.AbsUrl = entry.AbsUrl;
        paper.PdfUrl = entry.PdfUrl;
        paper.Doi = entry.Doi;
        paper.CodeUrl = entry.CodeUrl;
        paper.LastSeenUtc = now;

        var targetIds = entry.Categories.Select(code => categoryIdCache[code]).ToHashSet();
        var currentIds = paper.PaperCategories.Select(pc => pc.CategoryId).ToHashSet();

        foreach (var stale in paper.PaperCategories.Where(pc => !targetIds.Contains(pc.CategoryId)).ToList())
        {
            db.PaperCategories.Remove(stale);
            paper.PaperCategories.Remove(stale);
            changed = true;
        }

        foreach (var categoryId in targetIds.Except(currentIds))
        {
            paper.PaperCategories.Add(new PaperCategory { PaperId = paper.Id, CategoryId = categoryId });
            changed = true;
        }

        return changed;
    }
}
