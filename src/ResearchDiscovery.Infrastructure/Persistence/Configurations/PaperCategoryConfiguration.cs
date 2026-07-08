using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ResearchDiscovery.Domain.Entities;

namespace ResearchDiscovery.Infrastructure.Persistence.Configurations;

public class PaperCategoryConfiguration : IEntityTypeConfiguration<PaperCategory>
{
    public void Configure(EntityTypeBuilder<PaperCategory> builder)
    {
        builder.HasKey(pc => new { pc.PaperId, pc.CategoryId });

        // Drives the browse filter from the category side of the join.
        builder.HasIndex(pc => new { pc.CategoryId, pc.PaperId });

        builder.HasOne(pc => pc.Paper)
            .WithMany(p => p.PaperCategories)
            .HasForeignKey(pc => pc.PaperId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(pc => pc.Category)
            .WithMany(c => c.PaperCategories)
            .HasForeignKey(pc => pc.CategoryId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
