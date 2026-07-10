using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ResearchDiscovery.Domain.Entities;

namespace ResearchDiscovery.Infrastructure.Persistence.Configurations;

public class SearchEventConfiguration : IEntityTypeConfiguration<SearchEvent>
{
    public void Configure(EntityTypeBuilder<SearchEvent> builder)
    {
        builder.HasKey(e => e.Id);

        builder.Property(e => e.QueryText).HasMaxLength(2000);

        // Plan JSON is unbounded text, same portability posture as
        // AnalysisResult.ResultJson (no jsonb).
        builder.Property(e => e.PlanJson).IsRequired();

        builder.HasIndex(e => e.CreatedUtc);

        builder.HasMany(e => e.Results)
            .WithOne(r => r.SearchEvent)
            .HasForeignKey(r => r.SearchEventId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public class SearchEventResultConfiguration : IEntityTypeConfiguration<SearchEventResult>
{
    public void Configure(EntityTypeBuilder<SearchEventResult> builder)
    {
        builder.HasKey(r => new { r.SearchEventId, r.Rank });

        builder.Property(r => r.Proximity).HasMaxLength(16);

        builder.Property(r => r.Variant).HasMaxLength(1);

        builder.HasIndex(r => r.PaperId);

        builder.HasOne(r => r.Paper)
            .WithMany()
            .HasForeignKey(r => r.PaperId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public class InteractionEventConfiguration : IEntityTypeConfiguration<InteractionEvent>
{
    public void Configure(EntityTypeBuilder<InteractionEvent> builder)
    {
        builder.HasKey(e => e.Id);

        builder.Property(e => e.Type).HasConversion<string>().HasMaxLength(32);

        builder.HasIndex(e => e.CreatedUtc);
        builder.HasIndex(e => e.PaperId);

        builder.HasOne(e => e.Paper)
            .WithMany()
            .HasForeignKey(e => e.PaperId)
            .OnDelete(DeleteBehavior.Cascade);

        // No FK to SearchEvents: interactions must survive telemetry pruning,
        // and a dangling SearchEventId is harmless in an append-only log.
    }
}
