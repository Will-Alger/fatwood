using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ResearchDiscovery.Domain.Entities;

namespace ResearchDiscovery.Infrastructure.Persistence.Configurations;

public class PaperSignalConfiguration : IEntityTypeConfiguration<PaperSignal>
{
    public void Configure(EntityTypeBuilder<PaperSignal> builder)
    {
        builder.HasKey(s => s.PaperId);

        builder.HasOne(s => s.Paper)
            .WithOne(p => p.Signal)
            .HasForeignKey<PaperSignal>(s => s.PaperId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
