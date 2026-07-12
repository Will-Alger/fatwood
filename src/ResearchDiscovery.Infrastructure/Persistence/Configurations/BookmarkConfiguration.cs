using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ResearchDiscovery.Domain.Entities;

namespace ResearchDiscovery.Infrastructure.Persistence.Configurations;

public class BookmarkConfiguration : IEntityTypeConfiguration<Bookmark>
{
    public void Configure(EntityTypeBuilder<Bookmark> builder)
    {
        builder.HasKey(b => b.Id);

        // One bookmark per (user, paper). NULL UserId = legacy pre-account
        // rows awaiting the bootstrap admin's claim.
        builder.HasIndex(b => new { b.UserId, b.PaperId }).IsUnique();

        builder.HasOne(b => b.Paper)
            .WithMany(p => p.Bookmarks)
            .HasForeignKey(b => b.PaperId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(b => b.User)
            .WithMany()
            .HasForeignKey(b => b.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
