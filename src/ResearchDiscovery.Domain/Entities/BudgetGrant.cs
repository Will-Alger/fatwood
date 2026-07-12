namespace ResearchDiscovery.Domain.Entities;

/// <summary>
/// A credit added to a user's LLM budget. Remaining budget is always derived
/// (sum of grants minus sum of usage), never stored — an append-only ledger
/// a future payment provider can join by just inserting more grant rows.
/// Amounts are integer micro-dollars (1_000_000 = $1); money is never a float.
/// </summary>
public class BudgetGrant
{
    public long Id { get; set; }

    public long UserId { get; set; }

    public AppUser User { get; set; } = null!;

    public long AmountMicros { get; set; }

    /// <summary>Machine-readable origin: "signup", "admin", later "purchase".</summary>
    public string Reason { get; set; } = string.Empty;

    /// <summary>Admin who issued a manual grant; null for automatic grants.</summary>
    public long? GrantedByUserId { get; set; }

    public DateTimeOffset CreatedUtc { get; set; }
}
