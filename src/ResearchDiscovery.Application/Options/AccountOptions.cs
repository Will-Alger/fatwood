using System.ComponentModel.DataAnnotations;

namespace ResearchDiscovery.Application.Options;

/// <summary>
/// Account provisioning and platform-spend policy. All abuse controls that
/// bound cost live here so they are visible and tunable in one place.
/// </summary>
public class AccountOptions
{
    public const string SectionName = "Accounts";

    /// <summary>
    /// Feature flag: when true, new accounts stay inactive (no spend, no
    /// writes) until they redeem an invite code. Flippable without a deploy.
    /// </summary>
    public bool RequireInviteCode { get; set; }

    /// <summary>Signup budget grant in micro-dollars (default $1).</summary>
    [Range(0, 100_000_000)]
    public long StarterGrantMicros { get; set; } = 1_000_000;

    /// <summary>
    /// Circuit breaker independent of the dollar budget: max platform-billed
    /// LLM calls per user per UTC day. A runaway client hits this long before
    /// it drains a large grant.
    /// </summary>
    [Range(1, 100_000)]
    public int DailyCallCap { get; set; } = 200;

    /// <summary>
    /// Emails promoted to Admin on first sign-in. Bootstraps the first admin
    /// before any admin UI exists to do it.
    /// </summary>
    public IReadOnlyList<string> BootstrapAdminEmails { get; set; } = [];
}
