using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ResearchDiscovery.Application.Abstractions;
using ResearchDiscovery.Application.Options;
using ResearchDiscovery.Domain.Entities;
using ResearchDiscovery.Infrastructure.Persistence;

namespace ResearchDiscovery.Infrastructure.Ingestion;

/// <summary>
/// Cross-process ingestion lease backed by a single database row with a Guid
/// concurrency token. The CLI backfill and the in-web scheduler are separate
/// processes, so mutual exclusion must live in shared state — an in-process
/// semaphore cannot serialize them. The optimistic-concurrency update makes
/// the acquire atomic on any EF Core provider.
/// </summary>
public class DbIngestionLockManager(
    IDbContextFactory<AppDbContext> dbFactory,
    IOptions<IngestionOptions> options,
    ILogger<DbIngestionLockManager> logger) : IIngestionLockManager
{
    public async Task<IAsyncDisposable?> TryAcquireAsync(string holder, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var staleAfter = TimeSpan.FromMinutes(options.Value.LockStaleAfterMinutes);
        var stamp = Guid.NewGuid();

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var row = await db.IngestionLocks
            .SingleOrDefaultAsync(l => l.Id == IngestionLock.SingletonId, ct);

        if (row is null)
        {
            db.IngestionLocks.Add(new IngestionLock
            {
                Id = IngestionLock.SingletonId,
                Holder = holder,
                AcquiredUtc = now,
                Stamp = stamp,
            });

            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException)
            {
                // Another process inserted the row first and holds the lease.
                return null;
            }

            return new Lease(this, stamp);
        }

        if (row.Holder is not null)
        {
            if (now - row.AcquiredUtc < staleAfter)
            {
                return null;
            }

            logger.LogWarning(
                "Taking over stale ingestion lease held by {Holder} since {AcquiredUtc}",
                row.Holder, row.AcquiredUtc);
        }

        row.Holder = holder;
        row.AcquiredUtc = now;
        row.Stamp = stamp;

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Someone else acquired between our read and write.
            return null;
        }

        return new Lease(this, stamp);
    }

    private async Task ReleaseAsync(Guid stamp)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var row = await db.IngestionLocks
            .SingleOrDefaultAsync(l => l.Id == IngestionLock.SingletonId);

        if (row is null || row.Stamp != stamp)
        {
            // A stale-lease takeover replaced our stamp; nothing to release.
            return;
        }

        row.Holder = null;
        row.AcquiredUtc = null;
        row.Stamp = Guid.NewGuid();

        try
        {
            await db.SaveChangesAsync();
        }
        catch (DbUpdateConcurrencyException)
        {
            // Lost a race with a takeover — the new holder owns the row now.
        }
    }

    private sealed class Lease(DbIngestionLockManager owner, Guid stamp) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() => await owner.ReleaseAsync(stamp);
    }
}
