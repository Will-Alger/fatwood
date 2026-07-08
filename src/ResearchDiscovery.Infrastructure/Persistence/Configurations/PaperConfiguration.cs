using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ResearchDiscovery.Domain.Entities;

namespace ResearchDiscovery.Infrastructure.Persistence.Configurations;

public class PaperConfiguration : IEntityTypeConfiguration<Paper>
{
    public void Configure(EntityTypeBuilder<Paper> builder)
    {
        builder.HasKey(p => p.Id);

        builder.Property(p => p.ArxivId).HasMaxLength(64).IsRequired();
        builder.HasIndex(p => p.ArxivId).IsUnique();

        builder.Property(p => p.Title).IsRequired();
        builder.Property(p => p.Abstract).IsRequired();
        builder.Property(p => p.Authors).IsRequired();
        builder.Property(p => p.AbsUrl).HasMaxLength(512).IsRequired();
        builder.Property(p => p.PdfUrl).HasMaxLength(512).IsRequired();
        builder.Property(p => p.Doi).HasMaxLength(255);

        // Backs the default browse sort (newest first) with a stable tiebreaker.
        builder.HasIndex(p => new { p.PublishedUtc, p.Id }).IsDescending(true, true);

        builder.HasOne(p => p.PrimaryCategory)
            .WithMany()
            .HasForeignKey(p => p.PrimaryCategoryId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
