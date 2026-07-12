using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ResearchDiscovery.Domain.Entities;

namespace ResearchDiscovery.Infrastructure.Persistence.Configurations;

public class LlmUsageEventConfiguration : IEntityTypeConfiguration<LlmUsageEvent>
{
    public void Configure(EntityTypeBuilder<LlmUsageEvent> builder)
    {
        builder.HasKey(e => e.Id);

        builder.Property(e => e.Step).HasMaxLength(64).IsRequired();
        builder.Property(e => e.Model).HasMaxLength(128).IsRequired();

        builder.HasOne(e => e.User)
            .WithMany()
            .HasForeignKey(e => e.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        // Budget checks aggregate a user's spend; the daily circuit breaker
        // additionally filters by time — one composite index serves both.
        builder.HasIndex(e => new { e.UserId, e.CreatedUtc });
    }
}
