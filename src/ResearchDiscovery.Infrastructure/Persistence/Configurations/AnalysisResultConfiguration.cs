using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ResearchDiscovery.Domain.Entities;

namespace ResearchDiscovery.Infrastructure.Persistence.Configurations;

public class AnalysisResultConfiguration : IEntityTypeConfiguration<AnalysisResult>
{
    public void Configure(EntityTypeBuilder<AnalysisResult> builder)
    {
        builder.HasKey(a => a.Id);

        // Analyses are personalized: one per (user, paper). NULL UserId =
        // legacy/system rows, claimed by the bootstrap admin.
        builder.HasIndex(a => new { a.UserId, a.PaperId }).IsUnique();

        builder.HasOne(a => a.Paper)
            .WithMany(p => p.AnalysisResults)
            .HasForeignKey(a => a.PaperId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(a => a.User)
            .WithMany()
            .HasForeignKey(a => a.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Property(a => a.Model).HasMaxLength(128).IsRequired();

        // Plain text (not jsonb) so the column type is identical across providers.
        builder.Property(a => a.ResultJson).IsRequired();

        builder.Property(a => a.CompositeScore).HasPrecision(5, 2);
        builder.HasIndex(a => a.CompositeScore);
    }
}
