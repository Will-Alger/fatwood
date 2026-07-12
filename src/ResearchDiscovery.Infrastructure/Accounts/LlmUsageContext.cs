using ResearchDiscovery.Application.Abstractions;

namespace ResearchDiscovery.Infrastructure.Accounts;

/// <summary>Scoped holder; see <see cref="ILlmUsageContext"/> for the contract.</summary>
public class LlmUsageContext : ILlmUsageContext
{
    public long? UserId { get; set; }

    public bool UsedByoKey { get; set; }
}
