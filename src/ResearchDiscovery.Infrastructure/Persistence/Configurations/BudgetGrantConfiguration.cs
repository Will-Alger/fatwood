using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ResearchDiscovery.Domain.Entities;

namespace ResearchDiscovery.Infrastructure.Persistence.Configurations;

public class BudgetGrantConfiguration : IEntityTypeConfiguration<BudgetGrant>
{
    public void Configure(EntityTypeBuilder<BudgetGrant> builder)
    {
        builder.HasKey(g => g.Id);

        builder.Property(g => g.Reason).HasMaxLength(64).IsRequired();

        builder.HasOne(g => g.User)
            .WithMany()
            .HasForeignKey(g => g.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(g => g.UserId);
    }
}
