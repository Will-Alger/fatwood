using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ResearchDiscovery.Domain.Entities;

namespace ResearchDiscovery.Infrastructure.Persistence.Configurations;

public class AnalysisResultConfiguration : IEntityTypeConfiguration<AnalysisResult>
{
    public void Configure(EntityTypeBuilder<AnalysisResult> builder)
    {
        builder.HasKey(a => a.Id);

        // 1:1 with papers, enforced at the schema level.
        builder.HasIndex(a => a.PaperId).IsUnique();

        builder.HasOne(a => a.Paper)
            .WithOne(p => p.AnalysisResult)
            .HasForeignKey<AnalysisResult>(a => a.PaperId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Property(a => a.Model).HasMaxLength(128).IsRequired();

        // Plain text (not jsonb) so the column type is identical across providers.
        builder.Property(a => a.ResultJson).IsRequired();

        builder.Property(a => a.CompositeScore).HasPrecision(5, 2);
        builder.HasIndex(a => a.CompositeScore);
    }
}
