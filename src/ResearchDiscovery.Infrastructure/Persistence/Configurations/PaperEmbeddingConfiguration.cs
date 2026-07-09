using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ResearchDiscovery.Domain.Entities;

namespace ResearchDiscovery.Infrastructure.Persistence.Configurations;

public class PaperEmbeddingConfiguration : IEntityTypeConfiguration<PaperEmbedding>
{
    public void Configure(EntityTypeBuilder<PaperEmbedding> builder)
    {
        builder.HasKey(e => e.PaperId);

        builder.HasOne(e => e.Paper)
            .WithOne(p => p.Embedding)
            .HasForeignKey<PaperEmbedding>(e => e.PaperId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Property(e => e.ModelVersion).HasMaxLength(128).IsRequired();

        // Raw float32 bytes: bytea on Postgres, varbinary(max) on SQL Server.
        builder.Property(e => e.Vector).IsRequired();
    }
}
