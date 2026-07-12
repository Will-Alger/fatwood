using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ResearchDiscovery.Domain.Entities;

namespace ResearchDiscovery.Infrastructure.Persistence.Configurations;

public class InviteCodeConfiguration : IEntityTypeConfiguration<InviteCode>
{
    public void Configure(EntityTypeBuilder<InviteCode> builder)
    {
        builder.HasKey(c => c.Id);

        builder.Property(c => c.Code).HasMaxLength(64).IsRequired();
        builder.HasIndex(c => c.Code).IsUnique();
    }
}
