using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ResearchDiscovery.Domain.Entities;

namespace ResearchDiscovery.Infrastructure.Persistence.Configurations;

public class UserProfileConfiguration : IEntityTypeConfiguration<UserProfile>
{
    public void Configure(EntityTypeBuilder<UserProfile> builder)
    {
        builder.HasKey(p => p.Id);

        // One profile per account. UserId stays nullable only for legacy
        // pre-account rows, which the bootstrap admin claims on first
        // sign-in; Postgres treats NULLs as distinct in unique indexes.
        builder.HasIndex(p => p.UserId).IsUnique();
        builder.HasOne(p => p.User)
            .WithMany()
            .HasForeignKey(p => p.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Property(p => p.ExperienceSummary).IsRequired();
        builder.Property(p => p.Goals).IsRequired();
    }
}
