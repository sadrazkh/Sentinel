using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sentinel.Domain.Settings;

namespace Sentinel.Infrastructure.Persistence.Configurations;

public sealed class FeatureOverrideConfiguration : IEntityTypeConfiguration<FeatureOverride>
{
    public void Configure(EntityTypeBuilder<FeatureOverride> builder)
    {
        builder.ToTable("FeatureOverrides");

        builder.HasKey(entry => entry.Id);

        builder.Property(entry => entry.Name)
            .HasMaxLength(FeatureOverride.NameMaxLength)
            .IsRequired();

        builder.Property(entry => entry.ConcurrencyToken).IsConcurrencyToken();

        // One row per feature. Two would each claim to be the switch, and which one the gate read
        // would depend on row order — a coin toss deciding whether a feature is on.
        builder.HasIndex(entry => entry.Name).IsUnique();
    }
}
