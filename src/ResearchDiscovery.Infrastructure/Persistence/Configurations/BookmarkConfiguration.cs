using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ResearchDiscovery.Domain.Entities;

namespace ResearchDiscovery.Infrastructure.Persistence.Configurations;

public class BookmarkConfiguration : IEntityTypeConfiguration<Bookmark>
{
    public void Configure(EntityTypeBuilder<Bookmark> builder)
    {
        builder.HasKey(b => b.PaperId);

        builder.HasOne(b => b.Paper)
            .WithOne(p => p.Bookmark)
            .HasForeignKey<Bookmark>(b => b.PaperId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
