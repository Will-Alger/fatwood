namespace ResearchDiscovery.Application.Abstractions;

/// <summary>
/// Derives budget state from the grant/usage ledger and gates platform spend.
/// Admins are unlimited. BYO-key spend is never gated (it isn't our money).
/// </summary>
public interface IBudgetService
{
    Task<BudgetStatus> GetStatusAsync(long userId, CancellationToken ct);

    /// <summary>
    /// Throws <see cref="BudgetExceededException"/> when the user has no
    /// remaining budget or has tripped the daily call circuit breaker.
    /// </summary>
    Task EnsureCanSpendAsync(long userId, CancellationToken ct);
}

/// <summary>Money values are integer micro-dollars (1_000_000 = $1).</summary>
public record BudgetStatus(
    long GrantedMicros,
    long SpentMicros,
    long RemainingMicros,
    bool Unlimited);

public class BudgetExceededException(string message) : Exception(message);
