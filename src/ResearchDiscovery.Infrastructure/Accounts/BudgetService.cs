using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ResearchDiscovery.Application.Abstractions;
using ResearchDiscovery.Application.Options;
using ResearchDiscovery.Domain.Entities;
using ResearchDiscovery.Infrastructure.Persistence;

namespace ResearchDiscovery.Infrastructure.Accounts;

public class BudgetService(
    IDbContextFactory<AppDbContext> dbFactory,
    IOptions<AccountOptions> accountOptions) : IBudgetService
{
    public async Task<BudgetStatus> GetStatusAsync(long userId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var role = await db.AppUsers
            .Where(u => u.Id == userId)
            .Select(u => u.Role)
            .FirstOrDefaultAsync(ct);

        var granted = await db.BudgetGrants
            .Where(g => g.UserId == userId)
            .SumAsync(g => (long?)g.AmountMicros, ct) ?? 0;

        // BYO-key calls are the user's own money — they never debit the
        // platform budget, so they are excluded from spend.
        var spent = await db.LlmUsageEvents
            .Where(e => e.UserId == userId && !e.UsedByoKey)
            .SumAsync(e => (long?)e.CostMicros, ct) ?? 0;

        return new BudgetStatus(
            granted, spent, Math.Max(0, granted - spent), Unlimited: role == UserRole.Admin);
    }

    public async Task EnsureCanSpendAsync(long userId, CancellationToken ct)
    {
        var status = await GetStatusAsync(userId, ct);
        if (status.Unlimited)
        {
            return;
        }

        if (status.RemainingMicros <= 0)
        {
            throw new BudgetExceededException(
                "Your free budget is used up. An admin can top it up, or add your own Anthropic API key in Settings.");
        }

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var todayStart = new DateTimeOffset(DateTime.UtcNow.Date, TimeSpan.Zero);
        var callsToday = await db.LlmUsageEvents
            .CountAsync(e => e.UserId == userId && !e.UsedByoKey && e.CreatedUtc >= todayStart, ct);

        if (callsToday >= accountOptions.Value.DailyCallCap)
        {
            throw new BudgetExceededException(
                "Daily usage limit reached. It resets at midnight UTC.");
        }
    }
}
