using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ResearchDiscovery.Domain.Entities;

namespace ResearchDiscovery.Infrastructure.Persistence.Configurations;

public class CategoryIngestionStateConfiguration : IEntityTypeConfiguration<CategoryIngestionState>
{
    public void Configure(EntityTypeBuilder<CategoryIngestionState> builder)
    {
        builder.HasKey(s => s.CategoryId);

        builder.HasOne(s => s.Category)
            .WithOne()
            .HasForeignKey<CategoryIngestionState>(s => s.CategoryId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
