using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ResearchDiscovery.Domain.Entities;

namespace ResearchDiscovery.Infrastructure.Persistence.Configurations;

public class AdminActionLogConfiguration : IEntityTypeConfiguration<AdminActionLog>
{
    public void Configure(EntityTypeBuilder<AdminActionLog> builder)
    {
        builder.HasKey(a => a.Id);

        builder.Property(a => a.Action).HasMaxLength(64).IsRequired();

        builder.HasIndex(a => a.CreatedUtc);
    }
}
