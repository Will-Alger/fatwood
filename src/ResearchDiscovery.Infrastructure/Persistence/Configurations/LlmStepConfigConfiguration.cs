using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ResearchDiscovery.Domain.Entities;

namespace ResearchDiscovery.Infrastructure.Persistence.Configurations;

public class LlmStepConfigConfiguration : IEntityTypeConfiguration<LlmStepConfig>
{
    public void Configure(EntityTypeBuilder<LlmStepConfig> builder)
    {
        builder.HasKey(c => c.Step);

        builder.Property(c => c.Step).HasMaxLength(64);
        builder.Property(c => c.ModelId).HasMaxLength(128).IsRequired();
    }
}
