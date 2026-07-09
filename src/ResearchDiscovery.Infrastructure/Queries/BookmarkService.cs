using Microsoft.EntityFrameworkCore;
using ResearchDiscovery.Application.Abstractions;
using ResearchDiscovery.Domain.Entities;
using ResearchDiscovery.Infrastructure.Persistence;

namespace ResearchDiscovery.Infrastructure.Queries;

public class BookmarkService(AppDbContext db) : IBookmarkService
{
    public async Task<bool> SetAsync(string arxivId, bool bookmarked, CancellationToken ct)
    {
        var paper = await db.Papers
            .Include(p => p.Bookmark)
            .SingleOrDefaultAsync(p => p.ArxivId == arxivId, ct);

        if (paper is null)
        {
            return false;
        }

        if (bookmarked && paper.Bookmark is null)
        {
            db.Bookmarks.Add(new Bookmark
            {
                PaperId = paper.Id,
                CreatedUtc = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync(ct);
        }
        else if (!bookmarked && paper.Bookmark is not null)
        {
            db.Bookmarks.Remove(paper.Bookmark);
            await db.SaveChangesAsync(ct);
        }

        return true;
    }
}
