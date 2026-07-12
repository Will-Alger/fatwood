namespace ResearchDiscovery.Domain.Entities;

/// <summary>
/// Signup gate token. Only consulted when Signup:RequireInviteCode is on:
/// new accounts stay inactive until they redeem one. Codes are multi-use up
/// to MaxUses so one code can be handed to a group.
/// </summary>
public class InviteCode
{
    public long Id { get; set; }

    public string Code { get; set; } = string.Empty;

    public int MaxUses { get; set; }

    public int UsedCount { get; set; }

    public DateTimeOffset? ExpiresUtc { get; set; }

    public long? CreatedByUserId { get; set; }

    public DateTimeOffset CreatedUtc { get; set; }
}
