using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ResearchDiscovery.Domain.Entities;

namespace ResearchDiscovery.Infrastructure.Persistence.Configurations;

public class IngestionLockConfiguration : IEntityTypeConfiguration<IngestionLock>
{
    public void Configure(EntityTypeBuilder<IngestionLock> builder)
    {
        builder.HasKey(l => l.Id);

        builder.Property(l => l.Id).ValueGeneratedNever();

        builder.Property(l => l.Holder).HasMaxLength(256);

        // Portable optimistic-concurrency token: acquire/release rotate the
        // stamp, so two processes racing for the lease cannot both win.
        builder.Property(l => l.Stamp).IsConcurrencyToken();
    }
}
