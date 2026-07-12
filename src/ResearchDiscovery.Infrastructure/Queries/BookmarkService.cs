using Microsoft.EntityFrameworkCore;
using ResearchDiscovery.Application.Abstractions;
using ResearchDiscovery.Domain.Entities;
using ResearchDiscovery.Infrastructure.Persistence;

namespace ResearchDiscovery.Infrastructure.Queries;

public class BookmarkService(AppDbContext db) : IBookmarkService
{
    public async Task<bool> SetAsync(
        long userId, string arxivId, bool bookmarked, CancellationToken ct)
    {
        var paperId = await db.Papers
            .Where(p => p.ArxivId == arxivId)
            .Select(p => (long?)p.Id)
            .SingleOrDefaultAsync(ct);

        if (paperId is null)
        {
            return false;
        }

        var existing = await db.Bookmarks
            .SingleOrDefaultAsync(b => b.UserId == userId && b.PaperId == paperId, ct);

        if (bookmarked && existing is null)
        {
            db.Bookmarks.Add(new Bookmark
            {
                UserId = userId,
                PaperId = paperId.Value,
                CreatedUtc = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync(ct);
        }
        else if (!bookmarked && existing is not null)
        {
            db.Bookmarks.Remove(existing);
            await db.SaveChangesAsync(ct);
        }

        return true;
    }
}
