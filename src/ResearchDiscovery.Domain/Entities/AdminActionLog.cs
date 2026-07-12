namespace ResearchDiscovery.Domain.Entities;

/// <summary>
/// Audit trail for privileged operations (role changes, budget grants,
/// invite-code management). Append-only; never surfaced to non-admins.
/// </summary>
public class AdminActionLog
{
    public long Id { get; set; }

    public long ActorUserId { get; set; }

    /// <summary>Machine-readable action name, e.g. "user.role.set", "budget.grant".</summary>
    public string Action { get; set; } = string.Empty;

    public long? TargetUserId { get; set; }

    /// <summary>Free-form JSON detail (old/new values).</summary>
    public string? Detail { get; set; }

    public DateTimeOffset CreatedUtc { get; set; }
}
