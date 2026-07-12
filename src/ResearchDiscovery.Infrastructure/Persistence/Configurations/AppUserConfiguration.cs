using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ResearchDiscovery.Domain.Entities;

namespace ResearchDiscovery.Infrastructure.Persistence.Configurations;

public class AppUserConfiguration : IEntityTypeConfiguration<AppUser>
{
    public void Configure(EntityTypeBuilder<AppUser> builder)
    {
        builder.HasKey(u => u.Id);

        builder.Property(u => u.ExternalId).HasMaxLength(64).IsRequired();
        builder.HasIndex(u => u.ExternalId).IsUnique();

        builder.Property(u => u.Email).HasMaxLength(320).IsRequired();
        builder.HasIndex(u => u.Email);

        builder.Property(u => u.DisplayName).HasMaxLength(128).IsRequired();
        builder.Property(u => u.Role).HasConversion<string>().HasMaxLength(16);
        builder.Property(u => u.ThemePreference).HasMaxLength(16);
        builder.Property(u => u.EncryptedAnthropicKey).HasMaxLength(1024);
        builder.Property(u => u.AnthropicKeyLast4).HasMaxLength(8);
    }
}
